using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using AirlineDemo.Api.Evaluation;

namespace AirlineDemo.Api.Copilot;

/// <summary>The screening verdict of one untrusted record (PRD Addendum A screening contract).</summary>
public sealed record ScreeningVerdict(string State, IReadOnlyList<string> FlaggedFields)
{
    public const string Detected = "detected", Clear = "clear", UnavailableState = "unavailable", Service = "prompt-shields";
    public static readonly ScreeningVerdict Unavailable = new(UnavailableState, []);

    public JsonObject ToJson() => new()
    {
        ["state"] = State, ["service"] = Service, ["flaggedFields"] = new JsonArray([.. FlaggedFields.Select(field => (JsonNode)field)]),
    };
}

/// <summary>A record to screen: its identity and its string payload fields.</summary>
public sealed record ScreeningRecord(string SourceId, string RecordId, int RecordVersion, IReadOnlyDictionary<string, string> Fields);

/// <summary>Request-scoped screening state: one 3-second budget, and the first unavailable result stops further calls.</summary>
public sealed class ScreeningScope
{
    private const string ItemKey = "copilot.screening-scope";
    internal DateTimeOffset? Deadline;
    public bool Unavailable { get; internal set; }

    public static ScreeningScope For(HttpContext http)
    {
        if (http.Items[ItemKey] is ScreeningScope scope) return scope;
        scope = new ScreeningScope();
        http.Items[ItemKey] = scope;
        return scope;
    }
}

public interface ICopilotRecordScreener
{
    /// <summary>Returns one verdict per record, in input order. Never throws for service faults; it returns unavailable.</summary>
    Task<IReadOnlyList<ScreeningVerdict>> ScreenAsync(IReadOnlyList<ScreeningRecord> records, ScreeningScope scope, CancellationToken cancellationToken);
}

/// <summary>
/// Screens synthetic source record fields with Content Safety Prompt Shields through the APIM <c>contentsafety</c> API.
/// Record text goes only into the request body: never into a log, an exception, or the cache key (only its SHA-256).
/// </summary>
public sealed class CopilotRecordScreener : ICopilotRecordScreener
{
    public const string HttpClientName = "content-safety";
    public const string ApiVersion = "2024-09-01";
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(3);
    // Prompt Shields input limits: at most five documents and 10,000 characters per call.
    internal const int MaxDocuments = 5, MaxCharacters = 10_000;
    private const int MaxCacheEntries = 4096;

    private readonly Uri? _requestUri;
    private readonly TokenCredential _credential;
    private readonly string _scope;
    private readonly IHttpClientFactory _clients;
    private readonly TimeProvider _time;
    private readonly ILogger<CopilotRecordScreener> _logger;
    private readonly ConcurrentDictionary<string, bool> _cache = new(StringComparer.Ordinal);

    public CopilotRecordScreener(IConfiguration configuration, TokenCredential credential, IHttpClientFactory clients, TimeProvider time,
        ILogger<CopilotRecordScreener> logger)
    {
        var configured = configuration["ContentSafety:Endpoint"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var endpoint = ModelGatewayTokenomics.RequirePrivateGatewayEndpoint(configured);
            _requestUri = new Uri(endpoint.AbsoluteUri.TrimEnd('/') + $"/text:shieldPrompt?api-version={ApiVersion}");
        }
        _credential = credential;
        _scope = ModelGatewayTokenomics.TokenScope(configuration["ModelGateway:Audience"]);
        _clients = clients;
        _time = time;
        _logger = logger;
    }

    internal IEnumerable<string> CacheKeys => _cache.Keys;

    public async Task<IReadOnlyList<ScreeningVerdict>> ScreenAsync(IReadOnlyList<ScreeningRecord> records, ScreeningScope scope,
        CancellationToken cancellationToken)
    {
        // One document per string field (split at the per-call character limit), in ordinal field-name order.
        var fields = records.Select(record => record.Fields.OrderBy(field => field.Key, StringComparer.Ordinal)
            .Select(field => new Field(Key(record, field.Key, field.Value), field.Key, field.Value)).ToArray()).ToArray();
        var pending = fields.SelectMany(list => list).Where(field => !_cache.ContainsKey(field.CacheKey))
            .DistinctBy(field => field.CacheKey).ToList();
        if (pending.Count > 0 && !scope.Unavailable)
        {
            if (_requestUri is null) MarkUnavailable(scope, "endpoint-unset");
            else await ScreenPendingAsync(pending, scope, cancellationToken);
        }
        return [.. fields.Select(list =>
        {
            if (list.Any(field => !_cache.ContainsKey(field.CacheKey))) return ScreeningVerdict.Unavailable;
            var flagged = list.Where(field => _cache[field.CacheKey]).Select(field => field.Name).ToArray();
            return new ScreeningVerdict(flagged.Length > 0 ? ScreeningVerdict.Detected : ScreeningVerdict.Clear, flagged);
        })];
    }

    private async Task ScreenPendingAsync(List<Field> pending, ScreeningScope scope, CancellationToken cancellationToken)
    {
        scope.Deadline ??= _time.GetUtcNow() + Budget;
        var remaining = scope.Deadline.Value - _time.GetUtcNow();
        if (remaining <= TimeSpan.Zero) { MarkUnavailable(scope, "timeout"); return; }
        using var timeout = new CancellationTokenSource(remaining, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            var token = await _credential.GetTokenAsync(new TokenRequestContext([_scope]), linked.Token);
            var client = _clients.CreateClient(HttpClientName);
            var detected = new Dictionary<string, bool>(StringComparer.Ordinal);
            var run = $"screen-{Guid.NewGuid():N}";
            var call = 0;
            foreach (var batch in Batches(pending))
            {
                var flags = await CallAsync(client, token.Token, run, ++call, [.. batch.Select(piece => piece.Text)], linked.Token);
                if (flags is null) { MarkUnavailable(scope, "response"); return; }
                for (var i = 0; i < batch.Count; i++)
                    detected[batch[i].CacheKey] = detected.GetValueOrDefault(batch[i].CacheKey) || flags[i];
            }
            if (_cache.Count + detected.Count > MaxCacheEntries) _cache.Clear();
            foreach (var (key, value) in detected) _cache[key] = value;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            MarkUnavailable(scope, "timeout");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Only the exception type is logged: a service or transport message could echo record text.
            MarkUnavailable(scope, exception.GetType().Name);
        }
    }

    private async Task<bool[]?> CallAsync(HttpClient client, string token, string run, int call, string[] documents, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _requestUri)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { userPrompt = "", documents }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        // The gateway's contentsafety route requires the same seven correlation headers as the model route.
        var correlation = new GatewayCorrelation("copilot", run, "copilot-screening", "copilot-none", $"prompt-shields-{ApiVersion}",
            "copilot-v1", $"shield-{call}");
        ModelGatewayTokenomics.ValidateCorrelation(correlation);
        string[] values = [correlation.Arm, correlation.Run, correlation.Package, correlation.Case, correlation.ModelConfiguration,
            correlation.SuiteVersion, correlation.CallIdentity];
        for (var i = 0; i < values.Length; i++) request.Headers.Add(GatewayCorrelationHeadersPolicy.HeaderNames[i], values[i]);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Content screening returned status {Status}.", (int)response.StatusCode);
            return null;
        }
        try
        {
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (body?["documentsAnalysis"] is not JsonArray analysis || analysis.Count != documents.Length) return null;
            return [.. analysis.Select(item => item?["attackDetected"]?.GetValue<bool>() ?? throw new FormatException())];
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException) { return null; }
    }

    // Splits each field into pieces of at most MaxCharacters and packs pieces into calls within both per-call limits.
    private static IEnumerable<List<(string CacheKey, string Text)>> Batches(List<Field> pending)
    {
        var batch = new List<(string CacheKey, string Text)>();
        var characters = 0;
        foreach (var field in pending)
        {
            for (var offset = 0; offset < Math.Max(field.Text.Length, 1); offset += MaxCharacters)
            {
                var text = field.Text.Length == 0 ? "" : field.Text.Substring(offset, Math.Min(MaxCharacters, field.Text.Length - offset));
                if (batch.Count == MaxDocuments || characters + text.Length > MaxCharacters) { yield return batch; (batch, characters) = ([], 0); }
                batch.Add((field.CacheKey, text));
                characters += text.Length;
            }
        }
        if (batch.Count > 0) yield return batch;
    }

    private void MarkUnavailable(ScreeningScope scope, string reason)
    {
        scope.Unavailable = true;
        _logger.LogWarning("Content screening unavailable ({Reason}); records are shown with a neutral state.", reason);
    }

    private static string Key(ScreeningRecord record, string field, string text) => string.Join('\u001f', record.SourceId, record.RecordId,
        record.RecordVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), field,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))));

    private sealed record Field(string CacheKey, string Name, string Text);
}

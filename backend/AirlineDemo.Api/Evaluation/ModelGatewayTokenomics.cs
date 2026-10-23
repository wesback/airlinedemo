using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using Azure.Core;
using Microsoft.Extensions.AI;

namespace AirlineDemo.Api.Evaluation;

public sealed record GatewayUsage(long? InputTokens, long? OutputTokens, long? TotalTokens, string Status)
{
    public static GatewayUsage Unavailable() => new(null, null, null, "unavailable");
}

public sealed record GatewayTokenRateSchedule(decimal InputCostPerMillionTokens, decimal OutputCostPerMillionTokens);

public sealed record GatewayTokenMetrics(long? InputTokens, long? OutputTokens, long? TotalTokens, int ModelCallCount,
    int RetryCount, long LatencyMilliseconds, bool Throttled, string ModelConfiguration, decimal? EstimatedCost,
    GatewayUsage Usage);

public sealed record GatewayCorrelation(string Arm, string Run, string Package, string Case, string ModelConfiguration,
    string SuiteVersion, string CallIdentity)
{
    private static readonly Regex Identifier = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant);
    private static readonly Regex CardNumber = new(@"\d{13,19}", RegexOptions.CultureInvariant);
    public IReadOnlyList<string> Validate()
    {
        var values = new (string Name, string Value)[] { (nameof(Arm), Arm), (nameof(Run), Run), (nameof(Package), Package),
            (nameof(Case), Case), (nameof(ModelConfiguration), ModelConfiguration), (nameof(SuiteVersion), SuiteVersion), (nameof(CallIdentity), CallIdentity) };
        var forbiddenShape = new Regex("bearer\\s|authorization:|__host-|aspnetcore\\.cookies|pass(?:code)?|cookie|secret|access[_ -]?key|connection[_ -]?string|@[^\\s]+\\.[^\\s]+|raw[_ -]?prompt|prompt[_ -]?text|model[_ -]?response|document[_ -]?text|\\d{3}-\\d{2}-\\d{4}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        // Only Case carries an identifier chosen by a person; the other fields hold digests that can contain long digit runs.
        return values.Where(v => string.IsNullOrWhiteSpace(v.Value) || !Identifier.IsMatch(v.Value) || forbiddenShape.IsMatch(v.Value) ||
                                 (v.Name == nameof(Case) && CardNumber.IsMatch(v.Value))).Select(v => v.Name).ToArray();
    }
    public string ReplayIdentity() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', Arm, Run, Package, Case, ModelConfiguration, SuiteVersion, CallIdentity)))).ToLowerInvariant();
}

public sealed record GatewayQuota(long MaxInputTokensPerCall, long MaxOutputTokensPerCall, long MaxTotalTokensPerEvaluation,
    int MaxModelCalls, int MaxToolCalls, int MaxRetries, TimeSpan MaxElapsed);

public sealed record GatewayQuotaResult<T>(bool Accepted, string? DenialCode, GatewayUsage Usage, T? Result);

public sealed class GatewayQuotaGovernor
{
    private readonly GatewayQuota _quota;
    private long _evaluationInputTokens;
    private long _evaluationOutputTokens;
    private long _evaluationTotalTokens;
    private int _modelCalls, _toolCalls, _retries;
    private readonly long _startedTicks;

    public GatewayQuotaGovernor(GatewayQuota quota) { _quota = quota; _startedTicks = Environment.TickCount64; }

    public string? BeforeCall(long estimatedInputTokens, int retries = 0, int toolCalls = 0)
    {
        if (estimatedInputTokens > _quota.MaxInputTokensPerCall) return string.Format(ModelGatewayTokenomics.OverLimit, "input-tokens");
        if (retries > _quota.MaxRetries) return string.Format(ModelGatewayTokenomics.OverLimit, "retries");
        if (_modelCalls >= _quota.MaxModelCalls) return string.Format(ModelGatewayTokenomics.OverLimit, "model-calls");
        if (_toolCalls + toolCalls > _quota.MaxToolCalls) return string.Format(ModelGatewayTokenomics.OverLimit, "tool-calls");
        if (Environment.TickCount64 - _startedTicks > (long)_quota.MaxElapsed.TotalMilliseconds) return string.Format(ModelGatewayTokenomics.OverLimit, "elapsed-time");
        _modelCalls++; _toolCalls += toolCalls; _retries += retries;
        return null;
    }

    public string? Record(long? input, long? output)
    {
        var usage = ModelGatewayTokenomics.Usage(input, output, null);
        if (usage.Status == "unavailable") return null;
        _evaluationInputTokens += usage.InputTokens!.Value; _evaluationOutputTokens += usage.OutputTokens!.Value;
        _evaluationTotalTokens += usage.TotalTokens!.Value;
        if (usage.InputTokens > _quota.MaxInputTokensPerCall) return string.Format(ModelGatewayTokenomics.OverLimit, "input-tokens");
        if (usage.OutputTokens > _quota.MaxOutputTokensPerCall) return string.Format(ModelGatewayTokenomics.OverLimit, "output-tokens");
        if (_evaluationTotalTokens > _quota.MaxTotalTokensPerEvaluation) return string.Format(ModelGatewayTokenomics.OverLimit, "total-tokens");
        return null;
    }

    public GatewayUsage ExactUsage() => new(_evaluationInputTokens, _evaluationOutputTokens,
        _evaluationTotalTokens == 0 ? null : _evaluationTotalTokens, _evaluationTotalTokens == 0 ? "unavailable" : "available");
}

public static class ModelGatewayTokenomics
{
    public const string PrivateEndpointRequired = "model route rejected: direct Azure OpenAI endpoints are forbidden";
    public const string MissingUsage = "unavailable";
    public const string DefaultAudience = "api://airline-demo-model-gateway";
    public const string OverLimit = "gateway quota exceeded by one unit: {0}";

    public static Uri RequirePrivateGatewayEndpoint(string? configured)
    {
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.UserInfo.Length > 0 || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException(PrivateEndpointRequired);
        var host = uri.Host.ToLowerInvariant();
        if (host.EndsWith(".openai.azure.com", StringComparison.Ordinal) || host == "openai.azure.com")
            throw new InvalidOperationException(PrivateEndpointRequired);
        if (string.IsNullOrWhiteSpace(host) || !host.Contains('.')) throw new InvalidOperationException(PrivateEndpointRequired);
        return uri;
    }

    /// <summary>The token scope whose issued token audience is the APIM policy audience.</summary>
    public static string TokenScope(string? audience)
    {
        var value = string.IsNullOrWhiteSpace(audience) ? DefaultAudience : audience.Trim().TrimEnd('/');
        return value + "/.default";
    }

    public static void ValidateCorrelation(GatewayCorrelation correlation)
    {
        ArgumentNullException.ThrowIfNull(correlation);
        var invalid = correlation.Validate();
        if (invalid.Count > 0) throw new ArgumentException("correlation fields refused: " + string.Join(",", invalid));
    }

    public static decimal? EstimateCost(GatewayUsage usage, GatewayTokenRateSchedule rates)
    {
        if (usage.Status == MissingUsage || !usage.InputTokens.HasValue || !usage.OutputTokens.HasValue) return null;
        return Math.Round((usage.InputTokens.Value * rates.InputCostPerMillionTokens +
                           usage.OutputTokens.Value * rates.OutputCostPerMillionTokens) / 1_000_000m, 6,
            MidpointRounding.AwayFromZero);
    }

    public static GatewayUsage Usage(long? input, long? output, long? total)
    {
        if (!input.HasValue && !output.HasValue && !total.HasValue) return GatewayUsage.Unavailable();
        var i = input ?? 0; var o = output ?? 0; var t = total ?? i + o;
        if (i < 0 || o < 0 || t < 0) throw new ArgumentOutOfRangeException(nameof(total), "token usage cannot be negative");
        return new GatewayUsage(i, o, t, "available");
    }
}

/// <summary>Requests the gateway token scope whatever scope the Azure OpenAI client asks for.</summary>
public sealed class GatewayScopedTokenCredential(TokenCredential inner, string scope) : TokenCredential
{
    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        inner.GetToken(Rescope(requestContext), cancellationToken);

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        inner.GetTokenAsync(Rescope(requestContext), cancellationToken);

    private TokenRequestContext Rescope(TokenRequestContext context) =>
        new([scope], context.ParentRequestId, context.Claims, context.TenantId, context.IsCaeEnabled);
}

public sealed class GatewayGovernedChatClient(IChatClient inner, GatewayCorrelation correlation, GatewayQuota quota,
    GatewayTokenRateSchedule? rates = null) : DelegatingChatClient(inner)
{
    private readonly GatewayQuotaGovernor _governor = new(quota);
    private int _modelCallCount;
    private int _retryCount;
    private long _latencyMilliseconds;
    public GatewayUsage Usage => _governor.ExactUsage();
    public GatewayTokenMetrics? Metrics { get; private set; }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ModelGatewayTokenomics.ValidateCorrelation(correlation);
        var prompt = string.Join('\n', messages.Select(message => message.Text));
        var estimatedInput = Encoding.UTF8.GetByteCount(prompt);
        var denial = _governor.BeforeCall(estimatedInput);
        if (denial is not null) throw new InvalidOperationException(denial);

        options ??= new ChatOptions();
        options.ModelId = correlation.ModelConfiguration;
        options.AdditionalProperties ??= new AdditionalPropertiesDictionary();
        foreach (var field in new[] { "Arm", "Run", "Package", "Case", "ModelConfiguration", "SuiteVersion", "CallIdentity" })
        {
            var value = field switch
            {
                "Arm" => correlation.Arm, "Run" => correlation.Run, "Package" => correlation.Package,
                "Case" => correlation.Case, "ModelConfiguration" => correlation.ModelConfiguration,
                "SuiteVersion" => correlation.SuiteVersion, "CallIdentity" => correlation.CallIdentity, _ => ""
            };
            options.AdditionalProperties[$"gateway.{field.ToLowerInvariant()}"] = value;
        }

        var started = Stopwatch.GetTimestamp();
        _modelCallCount++;
        var response = await base.GetResponseAsync(messages, options, cancellationToken);
        _latencyMilliseconds = (long)(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        var usage = ModelGatewayTokenomics.Usage(response.Usage?.InputTokenCount, response.Usage?.OutputTokenCount,
            response.Usage?.TotalTokenCount);
        var denialAfter = _governor.Record(response.Usage?.InputTokenCount, response.Usage?.OutputTokenCount);
        if (denialAfter is not null) throw new InvalidOperationException(denialAfter);
        var throttled = response.AdditionalProperties?.TryGetValue("gateway.throttled", out var throttle) == true &&
                        bool.TryParse(throttle?.ToString(), out var isThrottled) && isThrottled;
        var estimatedCost = rates is null ? null : ModelGatewayTokenomics.EstimateCost(usage, rates);
        Metrics = new GatewayTokenMetrics(usage.InputTokens, usage.OutputTokens, usage.TotalTokens,
            _modelCallCount, _retryCount, _latencyMilliseconds, throttled, correlation.ModelConfiguration, estimatedCost, usage);
        return response;
    }

    public void RecordRetry() => _retryCount++;
}

public sealed class GatewayReplayStore<T>
{
    private readonly Dictionary<string, byte[]> _results = new(StringComparer.Ordinal);
    private readonly HashSet<string> _baseIdentity = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions SnapshotOptions = new(JsonSerializerDefaults.Web);

    public bool TryReplay(GatewayCorrelation correlation, out T? result)
    {
        ModelGatewayTokenomics.ValidateCorrelation(correlation);
        var baseKey = string.Join('|', correlation.Arm, correlation.Run, correlation.Package, correlation.Case,
            correlation.SuiteVersion, correlation.CallIdentity);
        if (_baseIdentity.Contains(baseKey) && !_results.ContainsKey(correlation.ReplayIdentity()))
            throw new InvalidOperationException("gateway replay rejected: model configuration changed");
        if (!_results.TryGetValue(correlation.ReplayIdentity(), out var snapshot)) { result = default; return false; }
        result = JsonSerializer.Deserialize<T>(snapshot, SnapshotOptions);
        return true;
    }
    public void Store(GatewayCorrelation correlation, T result)
    {
        ModelGatewayTokenomics.ValidateCorrelation(correlation);
        ArgumentNullException.ThrowIfNull(result);
        _baseIdentity.Add(string.Join('|', correlation.Arm, correlation.Run, correlation.Package, correlation.Case,
            correlation.SuiteVersion, correlation.CallIdentity));
        _results[correlation.ReplayIdentity()] = JsonSerializer.SerializeToUtf8Bytes(result, SnapshotOptions);
    }
}

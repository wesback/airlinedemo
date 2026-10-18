using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Persistence;

namespace DemoCaseGenerator;

public static class RehearsalEvidence
{
    public static readonly string[] PropertyNames =
    ["repositoryRevision", "fixtureConfigurationVersion", "deploymentId", "traceReference", "decision", "accessibilityResult",
        "applicationBuild", "browser", "assistiveTechnology", "runTimestampUtc", "outcomeDurationSeconds", "liveSegmentDurationSeconds",
        "fallbackOutcome", "fallbackFixtureId", "fallbackApplicationVersion", "journeyStates"];

    private static readonly HashSet<string> PropertySet = new(PropertyNames, StringComparer.Ordinal);
    private static readonly string[] RequiredText = ["repositoryRevision", "fixtureConfigurationVersion", "deploymentId",
        "traceReference", "decision", "accessibilityResult", "applicationBuild", "browser", "assistiveTechnology"];
    private static readonly Regex Rfc3339UtcPattern = new(@"\A\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?Z\z", RegexOptions.CultureInvariant);

    public static void ValidateJson(string json) { using var document = JsonDocument.Parse(json); Validate(document.RootElement); }

    public static void Validate(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid("Evidence must be a JSON object.");
        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!PropertySet.Contains(property.Name) || !properties.TryAdd(property.Name, property.Value))
                throw Invalid($"Unexpected or duplicate property: {property.Name}.");
        if (properties.Count != PropertyNames.Length) throw Invalid("Evidence must contain exactly the 16 defined properties.");

        foreach (var name in RequiredText) RequireText(properties[name], name);
        if (!IsRfc3339Utc(properties["runTimestampUtc"])) throw Invalid("runTimestampUtc must be an RFC3339 UTC timestamp ending in Z.");
        foreach (var name in new[] { "outcomeDurationSeconds", "liveSegmentDurationSeconds" })
            if (properties[name].ValueKind != JsonValueKind.Number
                || !properties[name].TryGetDouble(out var seconds) || !double.IsFinite(seconds) || seconds < 0)
                throw Invalid($"{name} must be a finite non-negative JSON number.");

        var fallback = properties["fallbackOutcome"];
        if (fallback.ValueKind != JsonValueKind.String) throw Invalid("fallbackOutcome must be used or not-used.");
        switch (fallback.GetString())
        {
            case "used":
                RequireText(properties["fallbackFixtureId"], "fallbackFixtureId");
                RequireText(properties["fallbackApplicationVersion"], "fallbackApplicationVersion");
                break;
            case "not-used":
                RequireNull(properties["fallbackFixtureId"], "fallbackFixtureId");
                RequireNull(properties["fallbackApplicationVersion"], "fallbackApplicationVersion");
                break;
            default:
                throw Invalid("fallbackOutcome must be used or not-used.");
        }

        var journey = properties["journeyStates"];
        if (journey.ValueKind != JsonValueKind.Array || journey.GetArrayLength() == 0)
            throw Invalid("journeyStates must be a non-empty array of non-empty strings.");
        foreach (var state in journey.EnumerateArray()) RequireText(state, "journeyStates item");
    }

    private static bool IsRfc3339Utc(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) return false;
        var text = value.GetString();
        return text is not null && Rfc3339UtcPattern.IsMatch(text)
            && DateTime.TryParseExact(text[..19], "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    }

    private static void RequireText(JsonElement value, string name)
    { if (value.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(value.GetString())) throw Invalid($"{name} must be a non-empty string."); }

    private static void RequireNull(JsonElement value, string name)
    { if (value.ValueKind != JsonValueKind.Null) throw Invalid($"{name} must be null when fallbackOutcome is not-used."); }

    private static InvalidDataException Invalid(string message) => new(message);
}

public sealed record RehearsalTimingResult(DateTimeOffset OutcomeStartedAtUtc, DateTimeOffset OutcomeEndedAtUtc,
    double OutcomeDurationSeconds, DateTimeOffset LiveSegmentStartedAtUtc, DateTimeOffset LiveSegmentEndedAtUtc,
    double LiveSegmentDurationSeconds, bool FallbackUsed);

public sealed class RehearsalTimingCapture(TimeProvider clock)
{
    private sealed record Boundary(long Timestamp, DateTimeOffset Utc);
    private Boundary? _outcomeStart, _outcomeEnd, _liveStart, _liveEnd;

    public void MarkOutcomeStart() => _outcomeStart = Mark(_outcomeStart);
    public void MarkOutcomeEnd() => _outcomeEnd = MarkAfter(_outcomeStart, _outcomeEnd);
    public void MarkLiveSegmentStart() => _liveStart = MarkAfter(_outcomeEnd, _liveStart);
    public void MarkLiveSegmentEnd() => _liveEnd = MarkAfter(_liveStart, _liveEnd);

    public RehearsalTimingResult Complete()
    {
        if (_outcomeStart is null || _outcomeEnd is null || _liveStart is null || _liveEnd is null)
            throw new InvalidOperationException("All four timing boundaries must be observed.");
        var outcome = clock.GetElapsedTime(_outcomeStart!.Timestamp, _outcomeEnd!.Timestamp).TotalSeconds;
        var live = clock.GetElapsedTime(_liveStart!.Timestamp, _liveEnd!.Timestamp).TotalSeconds;
        return new(_outcomeStart.Utc, _outcomeEnd.Utc, outcome, _liveStart.Utc, _liveEnd.Utc,
            live, outcome > 20 || live > 90);
    }

    private Boundary Mark(Boundary? previous)
    {
        if (previous is not null) throw new InvalidOperationException("Timing boundary was already observed.");
        return new(clock.GetTimestamp(), clock.GetUtcNow().ToUniversalTime());
    }

    private Boundary MarkAfter(Boundary? start, Boundary? previous)
    {
        if (start is null || previous is not null) throw new InvalidOperationException("Timing boundaries are missing or out of order.");
        return new(clock.GetTimestamp(), clock.GetUtcNow().ToUniversalTime());
    }
}

public sealed record StoredTraceResponse(HttpStatusCode StatusCode, byte[] Content);

public interface IStoredTraceReader
{
    Task<StoredTraceResponse> ReadAsync(Uri baseUri, string sessionCookie, string caseId, string runId, string investigationId,
        CancellationToken cancellationToken);
}

public sealed class HttpStoredTraceReader(HttpClient client) : IStoredTraceReader
{
    public async Task<StoredTraceResponse> ReadAsync(Uri baseUri, string sessionCookie, string caseId, string runId,
        string investigationId, CancellationToken cancellationToken)
    {
        var path = $"/api/cases/{Uri.EscapeDataString(caseId)}/dashboard/traces/{Uri.EscapeDataString(runId)}/{Uri.EscapeDataString(investigationId)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, path));
        request.Headers.Add("Cookie", $"{DemoAuthEndpoints.CookieName}={sessionCookie}");
        using var response = await client.SendAsync(request, cancellationToken);
        return new(response.StatusCode, await response.Content.ReadAsByteArrayAsync(cancellationToken));
    }
}

public static class RehearsalTraceCapture
{
    private static readonly string[] TraceProperties = ["caseId", "runId", "investigationId", "basisVersion", "modelDeploymentName",
        "apiVersion", "region", "contentFilterSetting", "decision", "actorToken", "outcome", "steps"];
    private static readonly string[] StepProperties =
        ["sequence", "toolName", "documentId", "pageNumber", "citationCheckOutcome", "correctionCode", "policyHandover"];

    public static async Task<byte[]?> CaptureAsync(IStoredTraceReader reader, Uri baseUri, string sessionCookie,
        string caseId, string runId, string investigationId, CancellationToken cancellationToken = default)
    {
        var response = await reader.ReadAsync(baseUri, sessionCookie, caseId, runId, investigationId, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode != HttpStatusCode.OK) throw new HttpRequestException(
            $"Stored trace endpoint returned {(int)response.StatusCode}.", null, response.StatusCode);
        return SanitizeStoredTrace(response.Content, caseId, runId, investigationId);
    }

    private static byte[] SanitizeStoredTrace(byte[] content, string caseId, string runId, string investigationId)
    {
        using var json = JsonDocument.Parse(content);
        var root = json.RootElement;
        ExactProperties(root, TraceProperties);
        if (root.GetProperty("caseId").GetString() != caseId || root.GetProperty("runId").GetString() != runId || root.GetProperty("investigationId").GetString() != investigationId)
            throw new InvalidDataException("Stored trace identity does not match the requested case, run and investigation.");
        var steps = root.GetProperty("steps");
        if (steps.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Stored trace steps are invalid.");
        var expected = 1;
        foreach (var step in steps.EnumerateArray())
        {
            ExactProperties(step, StepProperties);
            if (step.GetProperty("sequence").GetInt32() != expected++) throw new InvalidDataException("Stored trace steps are not in append order.");
        }

        using var sanitized = new MemoryStream();
        using (var writer = new Utf8JsonWriter(sanitized))
        {
            writer.WriteStartObject();
            foreach (var property in root.EnumerateObject())
            {
                if (property.NameEquals("actorToken")) continue;
                writer.WritePropertyName(property.Name);
                property.Value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return sanitized.ToArray();
    }

    private static void ExactProperties(JsonElement value, string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Stored trace shape is invalid.");
        var names = value.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
        if (names.Length != expected.Length || !names.SequenceEqual(expected.Order(StringComparer.Ordinal)))
            throw new InvalidDataException("Stored trace contains an unexpected or missing property.");
    }
}

public static class RehearsalCommands
{
    public static async Task<int> RunAsync(Func<Task<int>> command)
    {
        try { return await command(); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException
            or UnauthorizedAccessException or HttpRequestException or JsonException or UriFormatException)
        {
            Console.Error.WriteLine($"capture failed: {ex.Message}");
            return 1;
        }
    }

    public static Task<int> CaptureTiming()
    {
        var output = OutputPath("REHEARSAL_TIMING_OUTPUT");
        var capture = new RehearsalTimingCapture(TimeProvider.System);
        Mark("rehearsal journey begins", capture.MarkOutcomeStart); Mark("first-screen outcome appears", capture.MarkOutcomeEnd);
        Mark("live segment begins", capture.MarkLiveSegmentStart); Mark("live segment ends", capture.MarkLiveSegmentEnd);
        var result = capture.Complete();
        var json = JsonSerializer.Serialize(new
        {
            outcomeStartedAtUtc = Utc(result.OutcomeStartedAtUtc), outcomeEndedAtUtc = Utc(result.OutcomeEndedAtUtc),
            result.OutcomeDurationSeconds, liveSegmentStartedAtUtc = Utc(result.LiveSegmentStartedAtUtc), liveSegmentEndedAtUtc = Utc(result.LiveSegmentEndedAtUtc), result.LiveSegmentDurationSeconds,
            fallbackOutcome = result.FallbackUsed ? "used" : "not-used",
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        WriteNew(output, json);
        Console.WriteLine(result.FallbackUsed ? "Fallback was used; the live timing criterion did not pass."
            : "Observed timing recorded; both live timing limits passed.");
        return Task.FromResult(0);
    }

    public static async Task<int> CaptureTraceAsync()
    {
        var output = OutputPath("REHEARSAL_TRACE_OUTPUT");
        var baseUri = new Uri(Required("REHEARSAL_BASE_URL"), UriKind.Absolute);
        if (baseUri.Scheme != Uri.UriSchemeHttps && !(baseUri.IsLoopback && baseUri.Scheme == Uri.UriSchemeHttp))
            throw new InvalidOperationException("REHEARSAL_BASE_URL must use HTTPS, except for a loopback development host.");
        var cookie = Required("REHEARSAL_SESSION_COOKIE"); var caseId = Required("REHEARSAL_CASE_ID");
        var runId = Required("REHEARSAL_RUN_ID"); var investigationId = Required("REHEARSAL_INVESTIGATION_ID");
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var content = await RehearsalTraceCapture.CaptureAsync(new HttpStoredTraceReader(client), baseUri, cookie, caseId, runId, investigationId);
        if (content is null) throw new InvalidOperationException("Trace was absent or outside the session's case scope; no capture was written.");
        WriteNew(output, content);
        Console.WriteLine("Stored trace response recorded.");
        return 0;
    }

    private static void Mark(string prompt, Action mark)
    {
        Console.Write($"Observe when {prompt}, then press Enter: ");
        if (Console.ReadLine() is null) throw new InvalidOperationException("Timing measurement was not provided."); mark();
    }

    private static string Required(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException($"{name} is required.") : value;
    }

    private static string OutputPath(string name)
    {
        var path = Path.GetFullPath(Required(name));
        if (File.Exists(path)) throw new IOException($"Refusing to overwrite existing capture: {path}"); return path;
    }

    private static void WriteNew(string path, string content) => WriteNew(path, System.Text.Encoding.UTF8.GetBytes(content));

    private static void WriteNew(string path, byte[] content)
    {
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); output.Write(content);
    }

    private static string Utc(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
}

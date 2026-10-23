using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AirlineDemo.Api.Evaluation;

public sealed record GatewayUsage(long? InputTokens, long? OutputTokens, long? TotalTokens, string Status)
{
    public static GatewayUsage Unavailable() => new(null, null, null, "unavailable");
}

public sealed record GatewayTokenMetrics(long InputTokens, long OutputTokens, long TotalTokens, int ModelCallCount,
    int RetryCount, long LatencyMilliseconds, bool Throttled, string ModelConfiguration, decimal? EstimatedCost,
    GatewayUsage Usage);

public sealed record GatewayCorrelation(string Arm, string Run, string Package, string Case, string ModelConfiguration,
    string SuiteVersion, string CallIdentity)
{
    private static readonly Regex Identifier = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant);
    public IReadOnlyList<string> Validate()
    {
        var values = new (string Name, string Value)[] { (nameof(Arm), Arm), (nameof(Run), Run), (nameof(Package), Package),
            (nameof(Case), Case), (nameof(ModelConfiguration), ModelConfiguration), (nameof(SuiteVersion), SuiteVersion), (nameof(CallIdentity), CallIdentity) };
        return values.Where(v => string.IsNullOrWhiteSpace(v.Value) || !Identifier.IsMatch(v.Value)).Select(v => v.Name).ToArray();
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

    public static void ValidateCorrelation(GatewayCorrelation correlation)
    {
        ArgumentNullException.ThrowIfNull(correlation);
        var invalid = correlation.Validate();
        if (invalid.Count > 0) throw new ArgumentException("correlation fields refused: " + string.Join(",", invalid));
    }

    public static GatewayUsage Usage(long? input, long? output, long? total)
    {
        if (!input.HasValue && !output.HasValue && !total.HasValue) return GatewayUsage.Unavailable();
        var i = input ?? 0; var o = output ?? 0; var t = total ?? i + o;
        if (i < 0 || o < 0 || t < 0) throw new ArgumentOutOfRangeException(nameof(total), "token usage cannot be negative");
        return new GatewayUsage(i, o, t, "available");
    }
}

public sealed class GatewayReplayStore<T>
{
    private readonly Dictionary<string, T> _results = new(StringComparer.Ordinal);
    private readonly HashSet<string> _baseIdentity = new(StringComparer.Ordinal);
    public bool TryReplay(GatewayCorrelation correlation, out T? result)
    {
        ModelGatewayTokenomics.ValidateCorrelation(correlation);
        var baseKey = string.Join('|', correlation.Arm, correlation.Run, correlation.Package, correlation.Case,
            correlation.SuiteVersion, correlation.CallIdentity);
        if (_baseIdentity.Contains(baseKey) && !_results.ContainsKey(correlation.ReplayIdentity()))
            throw new InvalidOperationException("gateway replay rejected: model configuration changed");
        return _results.TryGetValue(correlation.ReplayIdentity(), out result);
    }
    public void Store(GatewayCorrelation correlation, T result)
    {
        ModelGatewayTokenomics.ValidateCorrelation(correlation);
        _baseIdentity.Add(string.Join('|', correlation.Arm, correlation.Run, correlation.Package, correlation.Case,
            correlation.SuiteVersion, correlation.CallIdentity));
        _results[correlation.ReplayIdentity()] = result;
    }
}

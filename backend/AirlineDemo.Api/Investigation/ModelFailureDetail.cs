using System.ClientModel;

namespace AirlineDemo.Api.Investigation;

/// <summary>
/// One-line description of a failed model call for operator logs: exception type, HTTP status and the service's
/// error message, truncated. Never includes the prompt or document text, which the service does not echo in errors.
/// </summary>
public static class ModelFailureDetail
{
    public const int MaxMessageLength = 300;

    public static string Describe(Exception ex)
    {
        var root = ex is AggregateException { InnerException: { } inner } ? inner : ex;
        var type = root.GetType().Name;
        var message = string.Join(' ', (root.Message ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (message.Length > MaxMessageLength) message = message[..MaxMessageLength] + "…";
        return root switch
        {
            ClientResultException client => $"{type} status={client.Status}: {message}",
            HttpRequestException http => $"{type} status={(http.StatusCode is { } code ? (int)code : 0)}: {message}",
            _ => $"{type}: {message}",
        };
    }

    /// <summary>True when the model endpoint or the network failed, as opposed to a bug in this service.</summary>
    public static bool IsModelServiceFailure(Exception ex, CancellationToken requestAborted) =>
        ex is ClientResultException or HttpRequestException
        || (ex is OperationCanceledException && !requestAborted.IsCancellationRequested);
}

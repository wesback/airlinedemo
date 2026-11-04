using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AirlineDemo.Deployment;

public static class ManagedCreateRecovery
{
    public static async Task<string?> ResolveConversationAsync(string reservationId, string attemptId,
        Func<string?, CancellationToken, Task<JsonObject>> listPage, Func<bool> admitRequest, CancellationToken cancellationToken)
    {
        string? after = null;
        string? match = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!admitRequest()) return null;
                var page = await listPage(after, cancellationToken);
                if (page["data"] is not JsonArray data || data.Count > 100 ||
                    page["has_more"] is not JsonValue moreValue || !moreValue.TryGetValue<bool>(out var more)) return null;
                string? last = null;
                foreach (var node in data)
                {
                    if (node is not JsonObject item || item["id"] is not JsonValue idValue ||
                        !idValue.TryGetValue<string>(out var id) ||
                        !Regex.IsMatch(id, "^[A-Za-z0-9_-]{1,128}\\z", RegexOptions.CultureInvariant) || !seen.Add(id)) return null;
                    last = id;
                    if (item["metadata"] is not JsonObject metadata ||
                        metadata["reservation_id"]?.GetValue<string>() != reservationId ||
                        metadata["attempt_id"]?.GetValue<string>() != attemptId) continue;
                    if (match is not null) return null;
                    match = id;
                }
                if (!more) return match;
                if (last is null || page["last_id"]?.GetValue<string>() != last) return null;
                after = last;
            } while (true);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or HttpRequestException)
        {
            return null;
        }
    }
}

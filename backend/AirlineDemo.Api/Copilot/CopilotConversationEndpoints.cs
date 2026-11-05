using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Evaluation;
using AirlineDemo.Api.Foundry;
using AirlineDemo.Api.Persistence;

namespace AirlineDemo.Api.Copilot;

public static class CopilotConversationEndpoints
{
    private static readonly Regex RemoteId = new("^[A-Za-z0-9_-]{1,128}\\z", RegexOptions.CultureInvariant);

    public static IEndpointRouteBuilder MapCopilotConversationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/copilot/conversations/{handle}", ReadHistory);
        return app;
    }

    private static async Task<IResult> ReadHistory(string handle, HttpContext http, DemoSessionStore sessions,
        IManagedConversationStore store, IManagedProjectClient client, CopilotProjections projections,
        ICaseStateStore cases, TimeProvider time)
    {
        var session = DemoAuthEndpoints.CurrentSession(http, sessions);
        if (CopilotRequestGuard.Check(http, session) is { } denied) return CopilotRequestGuard.Error(denied.Status, denied.Code);
        if (!CopilotProjections.Roles.Contains(session!.User.Role, StringComparer.Ordinal))
            return CopilotRequestGuard.Error(403, "forbidden");
        if (!SqlManagedConversationStore.IsValidHandle(handle))
            return CopilotRequestGuard.Error(400, "invalid_conversation_handle");

        ManagedConversationSnapshot binding;
        try { binding = store.ReadManagedConversation(handle)!; }
        catch (ManagedConversationStoreException failure) { return Error(failure.Code); }
        catch (DbException) { return Error("conversation_store_unavailable"); }
        if (binding is null || binding.Tuple.UserId != session.User.Id || binding.Tuple.IsListing)
            return Error("conversation_not_found");

        ManagedAgentTarget target;
        try { target = client.Agent("copilot"); }
        catch (ManagedProjectException failure) { return Error(failure.Code); }
        catch (InvalidOperationException) { return Error("managed_agent_unconfigured"); }

        try
        {
            var verified = VerifyBinding(binding, http, sessions, store, projections, cases, target, time);
            if (verified.RemoteConversationId is null) throw new ManagedProjectException("conversation_unavailable");
            if (!RemoteId.IsMatch(verified.RemoteConversationId)) throw new ManagedProjectException("conversation_history_invalid");

            var correlation = new GatewayCorrelation("copilot", "history-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)),
                "managed-copilot", verified.Tuple.CaseId, target.ConfigurationId, "copilot-v1", "history-items");
            using var deadline = new CancellationTokenSource(SqlManagedConversationStore.TurnDeadline, time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, deadline.Token);
            var items = new JsonArray();
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            var itemIds = new HashSet<string>(StringComparer.Ordinal);
            string? after = null;
            var readAttempt = 0;
            async Task<JsonObject> ReadPage()
            {
                for (var attempt = 0; ; attempt++)
                {
                    _ = VerifyBinding(binding, http, sessions, store, projections, cases, target, time);
                    try
                    {
                        var result = await client.ListItemsAsync("copilot", verified.RemoteConversationId!, after,
                            correlation with { CallIdentity = $"history-{++readAttempt}" }, linked.Token);
                        _ = VerifyBinding(binding, http, sessions, store, projections, cases, target, time);
                        return result;
                    }
                    catch (ManagedProjectException failure) when (failure.RetryableRead)
                    {
                        _ = VerifyBinding(binding, http, sessions, store, projections, cases, target, time);
                        if (attempt == 2) throw new ManagedProjectException("copilot_retry_limit");
                        await Task.Delay(attempt == 0 ? TimeSpan.FromMilliseconds(500) : TimeSpan.FromMilliseconds(1000),
                            time, linked.Token);
                    }
                }
            }

            while (true)
            {
                var page = await ReadPage();
                if (page["data"] is not JsonArray data || data.Count > 100 ||
                    page["has_more"] is not JsonValue more || !more.TryGetValue<bool>(out var hasMore))
                    throw new ManagedProjectException("conversation_history_invalid");
                foreach (var item in data)
                {
                    if (item is not JsonObject entry || entry["id"] is not JsonValue idValue ||
                        !idValue.TryGetValue<string>(out var id) || !RemoteId.IsMatch(id) || !itemIds.Add(id))
                        throw new ManagedProjectException("conversation_history_invalid");
                    items.Add(entry.DeepClone());
                }
                if (!hasMore) break;
                if (data.Count == 0 || !cursors.Add(after = (string)data[^1]!["id"]!))
                    throw new ManagedProjectException("conversation_history_invalid");
            }

            var summaries = ManagedHistoryContract.CopilotSummaries(items.ToJsonString(), verified.Tuple.CaseId, target);
            if (verified.AdmittedTurnCount is < 1 or > 20 || summaries.Count != verified.AdmittedTurnCount)
                throw new ManagedProjectException("conversation_history_invalid");
            _ = VerifyBinding(binding, http, sessions, store, projections, cases, target, time);
            var body = new JsonObject
            {
                ["contractVersion"] = CopilotProjections.ContractVersion,
                ["conversationHandle"] = binding.Handle,
                ["conversationState"] = "active",
                ["turnCount"] = verified.AdmittedTurnCount,
                ["expiresAt"] = new DateTimeOffset(verified.ExpiresAtTicks, TimeSpan.Zero).ToString("O"),
                ["turns"] = new JsonArray([.. summaries.Select(summary => (JsonNode)new JsonObject
                {
                    ["question"] = summary.Question,
                    ["answer"] = summary.Answer,
                    ["citations"] = new JsonArray([.. summary.Citations.Select(citation => (JsonNode)new JsonObject
                    {
                        ["sourceId"] = citation.SourceId, ["recordId"] = citation.RecordId, ["recordVersion"] = citation.RecordVersion,
                    })]),
                })]),
            };
            return Results.Json(body);
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Error("copilot_timeout");
        }
        catch (ManagedProjectException failure)
        {
            if (failure.Code is "conversation_history_invalid" or "conversation_unavailable")
                Retire(store, binding);
            return Error(failure.Code);
        }
        catch (ManagedConversationStoreException failure) { return Error(failure.Code); }
        catch (DbException) { return Error("conversation_store_unavailable"); }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            Retire(store, binding);
            return Error("conversation_history_invalid");
        }
    }

    private static ManagedConversationSnapshot VerifyBinding(ManagedConversationSnapshot original, HttpContext http,
        DemoSessionStore sessions, IManagedConversationStore store, CopilotProjections projections,
        ICaseStateStore cases, ManagedAgentTarget target, TimeProvider time)
    {
        var current = store.ReadManagedConversation(original.Handle);
        var session = DemoAuthEndpoints.CurrentSession(http, sessions);
        if (session is null) throw new ManagedProjectException("unauthenticated");
        if (session.User.Id != original.Tuple.UserId || current is null || current.Tuple.UserId != original.Tuple.UserId)
            throw new ManagedProjectException("conversation_not_found");
        if (!CopilotProjections.Roles.Contains(session.User.Role, StringComparer.Ordinal))
            throw new ManagedProjectException("forbidden");
        if (projections.Authorize(session, current.Tuple.CaseId).Denied is { } denied)
            throw new ManagedProjectException(denied.Outcome == CopilotProjectionOutcome.Forbidden ? "forbidden" : "case_not_found");
        if (time.GetUtcNow().UtcTicks >= current.ExpiresAtTicks)
        {
            Retire(store, current);
            throw new ManagedProjectException("conversation_expired");
        }
        if (current.Retired) throw new ManagedProjectException("conversation_unavailable");

        ManagedConversationTuple? live;
        try { live = CopilotQuestions.BuildCurrentTuple(http, sessions, cases, projections, current.Tuple.CaseId, target); }
        catch (ManagedProjectException)
        {
            Retire(store, current);
            throw;
        }
        if (live != current.Tuple)
        {
            Retire(store, current);
            throw new ManagedProjectException("conversation_context_mismatch");
        }
        if (current.ActiveFence is not null)
        {
            if (current.TurnDeadlineTicks is null || time.GetUtcNow().UtcTicks >= current.TurnDeadlineTicks)
            {
                Retire(store, current);
                throw new ManagedProjectException("conversation_expired");
            }
            throw new ManagedProjectException("conversation_busy");
        }
        if (current.RowRevision != original.RowRevision) throw new ManagedProjectException("conversation_busy");
        return current;
    }

    private static void Retire(IManagedConversationStore store, ManagedConversationSnapshot binding)
    {
        try { store.RetireManagedConversation(binding.Handle, binding.Tuple, binding.RowRevision, binding.ActiveFence); }
        catch (ManagedConversationStoreException) { }
    }

    private static IResult Error(string code) => code switch
    {
        "invalid_conversation_handle" or "invalid_question" => CopilotRequestGuard.Error(400, code),
        "unauthenticated" => CopilotRequestGuard.Error(401, code),
        "forbidden" => CopilotRequestGuard.Error(403, code),
        "case_not_found" or "conversation_not_found" => CopilotRequestGuard.Error(404, code),
        "conversation_busy" or "conversation_context_mismatch" => CopilotRequestGuard.Error(409, code),
        "conversation_expired" or "conversation_turn_limit" or "conversation_unavailable" => CopilotRequestGuard.Error(410, code),
        "copilot_retry_limit" => CopilotRequestGuard.Error(429, code),
        "copilot_timeout" => CopilotRequestGuard.Error(504, code),
        "conversation_store_unavailable" or "managed_agent_unconfigured" or "model_unavailable" =>
            CopilotRequestGuard.Error(503, code),
        "managed_operation_ambiguous" or "conversation_history_invalid" => CopilotRequestGuard.Error(502, code),
        _ => CopilotRequestGuard.Error(502, "conversation_history_invalid"),
    };
}

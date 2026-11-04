using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AirlineDemo.Api.Copilot;
using AirlineDemo.Api.Diagnostics;
using AirlineDemo.Api.Evaluation;
using AirlineDemo.Api.Persistence;

namespace AirlineDemo.Api.Foundry;

public sealed record ManagedTurnConfiguration(string Instructions, string Configuration, string ToolSchemas);
public sealed record ManagedToolSelection(string CallId, string Name, JsonObject Arguments, string? RawArguments = null);
public sealed record ManagedTurnResult(string Text, string? ResponseId, IReadOnlyList<string> ItemIds, long? InputTokens, long? OutputTokens,
    CopilotInvocationResult? NativeResult = null);

internal sealed class ManagedToolTerminal(CopilotInvocationResult result, bool committed) : Exception
{
    internal CopilotInvocationResult Result { get; } = result;
    internal bool Committed { get; } = committed;
}

public sealed class ManagedTurnCoordinator(IManagedProjectClient client, ManagedConversationLifecycle lifecycle, TimeProvider time)
{
    public async Task<ManagedTurnResult> RunAsync(string workload, ManagedConversationSnapshot conversation,
        Func<ManagedConversationTuple?> currentTuple, GatewayCorrelation correlation, ManagedTurnConfiguration configuration,
        ManagedHistoryPolicy policy, JsonArray input,
        Func<ManagedToolSelection, CancellationToken, Task<JsonObject>> dispatch, CancellationToken cancellationToken)
    {
        var snapshot = (JsonArray)input.DeepClone();
        var target = new ManagedAgentTarget(conversation.Tuple.AgentName, conversation.Tuple.AgentVersion,
            conversation.Tuple.ConfigurationDigest);
        using var activity = TelemetryRedaction.StartManagedOperation("invoke_agent", target,
            conversation.RemoteConversationId);
        try
        {
            var result = await lifecycle.ExecuteTurnAsync(conversation, currentTuple, async token =>
            {
                var result = await ExecuteAsync(workload, conversation, currentTuple, correlation, configuration, policy, snapshot, dispatch, token);
                return (result, new ManagedTurnCompletionMetadata(result.ResponseId, result.ItemIds,
                    result.NativeResult is null ? ResultDigest(result.Text) : null));
            }, cancellationToken);
            if (result.ResponseId is { } responseId)
                activity?.SetTag("gen_ai.response.id", TelemetryRedaction.OpaqueReference(responseId));
            if (result.InputTokens is { } inputTokens)
                activity?.SetTag("gen_ai.usage.input_tokens", inputTokens);
            if (result.OutputTokens is { } outputTokens)
                activity?.SetTag("gen_ai.usage.output_tokens", outputTokens);
            return result;
        }
        catch (ManagedToolTerminal terminal) when (terminal.Committed)
        {
            return new("", null, [], null, null, terminal.Result);
        }
        catch (ManagedProjectException exception)
        {
            TelemetryRedaction.MarkManagedOperationFailed(activity, exception.Code);
            throw;
        }
    }

    private async Task<ManagedTurnResult> ExecuteAsync(string workload, ManagedConversationSnapshot conversation,
        Func<ManagedConversationTuple?> currentTuple, GatewayCorrelation correlation, ManagedTurnConfiguration configuration,
        ManagedHistoryPolicy policy, JsonArray input,
        Func<ManagedToolSelection, CancellationToken, Task<JsonObject>> dispatch, CancellationToken token)
    {
        if (policy.Workload is not null && policy.Workload != workload || policy.Context is not null && !policy.Context(conversation.Tuple))
            throw new ManagedProjectException("conversation_context_mismatch");
        var target = client.Agent(workload);
        if (target.Name != conversation.Tuple.AgentName || target.Version != conversation.Tuple.AgentVersion ||
            target.ConfigurationId != conversation.Tuple.ConfigurationDigest || correlation.ModelConfiguration != target.ConfigurationId)
            throw new ManagedProjectException("managed_agent_unconfigured");
        var remoteId = Identifier(conversation.RemoteConversationId);
        var history = new JsonArray();
        string? after = null;
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        var seenItems = new HashSet<string>(StringComparer.Ordinal);
        var request = 0;
        void Check()
        {
            token.ThrowIfCancellationRequested();
            if (currentTuple() != conversation.Tuple) throw new ManagedProjectException("conversation_context_mismatch");
        }
        GatewayCorrelation Next() => correlation with { CallIdentity = $"managed-{++request}" };
        async Task<JsonObject> Read(Func<GatewayCorrelation, Task<JsonObject>> action)
        {
            for (var retries = 0; ; retries++)
            {
                Check();
                try
                {
                    var page = await action(Next());
                    Check();
                    return page;
                }
                catch (ManagedProjectException failure) when (failure.RetryableRead)
                {
                    Check();
                    if (retries == 2) throw new ManagedProjectException("copilot_retry_limit");
                    await Task.Delay(TimeSpan.FromMilliseconds(retries == 0 ? 500 : 1000), time, token);
                    Check();
                }
            }
        }
        while (true)
        {
            var page = await Read(headers => client.ListItemsAsync(workload, remoteId, after, headers, token));
            if (page["data"] is not JsonArray data || data.Count > 100 || page["has_more"] is not JsonValue more ||
                !more.TryGetValue<bool>(out var hasMore)) throw new ManagedProjectException("conversation_history_invalid");
            foreach (var item in data)
            {
                if (item is not JsonObject entry) throw new ManagedProjectException("conversation_history_invalid");
                var id = Identifier(entry["id"]);
                if (!seenItems.Add(id)) throw new ManagedProjectException("conversation_history_invalid");
                history.Add(entry.DeepClone());
            }
            var validated = ManagedHistoryContract.Validate(history.ToJsonString(), policy);
            _ = ManagedHistoryContract.EstimateInputTokens(validated, configuration.Instructions, configuration.Configuration,
                configuration.ToolSchemas, input, target.Name, target.Version, target.ConfigurationId, remoteId);
            if (!hasMore) break;
            if (data.Count == 0 || !cursors.Add(after = Identifier(data[^1]?["id"])))
                throw new ManagedProjectException("conversation_history_invalid");
        }
        var storedCalls = history.OfType<JsonObject>().Where(item => (string?)item["type"] == "function_call")
            .Select(item => (string?)item["call_id"]).ToHashSet(StringComparer.Ordinal);
        var storedResults = history.OfType<JsonObject>().Where(item => (string?)item["type"] == "function_call_output")
            .Select(item => (string?)item["call_id"]).ToHashSet(StringComparer.Ordinal);
        if (!storedCalls.SetEquals(storedResults)) throw new ManagedProjectException("conversation_history_invalid");

        var pending = (JsonArray)input.DeepClone();
        var toolAttempts = 0;
        var allCallIds = history.OfType<JsonObject>().Where(item => (string?)item["type"] == "function_call")
            .Select(item => (string?)item["call_id"]).OfType<string>().ToHashSet(StringComparer.Ordinal);
        for (var submission = 1; ; submission++)
        {
            Check();
            if (submission > 8) throw new ManagedProjectException("copilot_model_limit");
            var previous = ManagedHistoryContract.Validate(history.ToJsonString(), policy);
            _ = ManagedHistoryContract.EstimateInputTokens(previous, configuration.Instructions, configuration.Configuration,
                configuration.ToolSchemas, pending, target.Name, target.Version, target.ConfigurationId, remoteId);
            AppendInput(history, pending, submission);
            _ = ManagedHistoryContract.Validate(history.ToJsonString(), policy);
            Check();
            var response = await client.CreateResponseAsync(workload, remoteId, pending, Next(), token);
            Check();
            var envelope = Response(response, remoteId, target);
            var calls = envelope.Output.OfType<JsonObject>().Where(item => (string?)item["type"] == "function_call").ToArray();
            foreach (var _ in calls)
                if (++toolAttempts > 20) throw new ManagedProjectException("copilot_tool_limit");
            foreach (var item in envelope.Output)
                history.Add(item?.DeepClone());
            _ = ManagedHistoryContract.Validate(history.ToJsonString(), policy);
            if (calls.Length == 0)
            {
                var text = new StringBuilder();
                var messages = envelope.Output.OfType<JsonObject>().Where(item => (string?)item["role"] == "assistant").ToArray();
                if (messages.Length != 1) throw new ManagedProjectException("managed_response_invalid");
                foreach (var item in messages)
                    foreach (var part in item["content"]!.AsArray()) text.Append((string?)part!["text"]);
                if (text.Length == 0) throw new ManagedProjectException("managed_response_invalid");
                return new(text.ToString(), envelope.Id, envelope.Output.Select(item => Identifier(item?["id"])).ToArray(),
                    envelope.InputTokens, envelope.OutputTokens);
            }
            if (envelope.Output.OfType<JsonObject>().Any(item => (string?)item["role"] == "assistant"))
                throw new ManagedProjectException("managed_response_invalid");
            pending = [];
            foreach (var call in calls)
            {
                var callId = Identifier(call["call_id"]);
                if (workload == "copilot" &&
                    !Regex.IsMatch(callId, "^[A-Za-z0-9_-]{1,64}\\z", RegexOptions.CultureInvariant))
                    throw new ManagedProjectException("managed_response_invalid");
                if (!allCallIds.Add(callId)) throw new ManagedProjectException("managed_response_invalid");
                var rawArguments = call["arguments"]!.GetValue<string>();
                var selection = new ManagedToolSelection(callId, call["name"]!.GetValue<string>(),
                    ParseArguments(rawArguments), rawArguments);
                Check();
                JsonObject output;
                using var toolActivity = TelemetryRedaction.StartManagedOperation("execute_tool", target,
                    remoteId, toolName: selection.Name, toolCallId: selection.CallId);
                try { output = await dispatch(selection, token); }
                catch (ManagedToolTerminal terminal) when (!terminal.Committed)
                {
                    Check();
                    return new("", envelope.Id, envelope.Output.Select(item => Identifier(item?["id"])).ToArray(),
                        envelope.InputTokens, envelope.OutputTokens, terminal.Result);
                }
                catch (Exception exception)
                {
                    TelemetryRedaction.MarkManagedOperationFailed(toolActivity,
                        exception is ManagedProjectException managed ? managed.Code : "tool_execution_failed");
                    throw;
                }
                Check();
                pending.Add(new JsonObject { ["type"] = "function_call_output", ["call_id"] = callId, ["output"] = output.ToJsonString() });
                var proposed = (JsonArray)history.DeepClone();
                AppendInput(proposed, pending, submission + 1);
                _ = ManagedHistoryContract.Validate(proposed.ToJsonString(), policy);
            }
        }
    }

    private static void AppendInput(JsonArray history, JsonArray input, int submission)
    {
        for (var index = 0; index < input.Count; index++)
        {
            var item = input[index]?.DeepClone() as JsonObject ?? throw new ManagedProjectException("conversation_history_invalid");
            if (item.ContainsKey("id") || item.ContainsKey("status")) throw new ManagedProjectException("conversation_history_invalid");
            item["id"] = $"local-{submission}-{index}";
            if (item["type"] is null) { item["type"] = "message"; item["status"] = "completed"; }
            history.Add(item);
        }
    }

    private sealed record Envelope(string Id, JsonArray Output, long? InputTokens, long? OutputTokens);

    private static Envelope Response(JsonObject response, string conversationId, ManagedAgentTarget target)
    {
        try { return ReadResponse(response, conversationId, target); }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException or JsonException)
        {
            throw new ManagedProjectException("managed_response_invalid");
        }
    }

    private static Envelope ReadResponse(JsonObject response, string conversationId, ManagedAgentTarget target)
    {
        if ((string?)response["incomplete_details"]?["reason"] == "content_filter")
            throw new ManagedProjectException("managed_content_filtered");
        if ((string?)response["incomplete_details"]?["reason"] == "max_output_tokens")
            throw new ManagedProjectException("copilot_output_token_limit");
        if (response["output"] is JsonArray candidates && candidates.OfType<JsonObject>()
            .Any(item => item["content"] is JsonArray parts && parts.OfType<JsonObject>().Any(part => (string?)part["type"] == "refusal")))
            throw new ManagedProjectException("managed_refusal");
        if ((string?)response["object"] != "response" || (string?)response["status"] != "completed" ||
            response["error"] is not null || response["incomplete_details"] is not null ||
            (string?)response["conversation"]?["id"] != conversationId || response["output"] is not JsonArray output ||
            (string?)response["agent_reference"]?["type"] != "agent_reference" ||
            (string?)response["agent_reference"]?["name"] != target.Name || (string?)response["agent_reference"]?["version"] != target.Version ||
            response["max_output_tokens"] is not JsonValue maximum || !maximum.TryGetValue<int>(out var max) || max != 2000 ||
            (string?)response["truncation"] != "disabled" || response["background"]?.GetValue<bool>() != false)
            throw new ManagedProjectException("managed_response_invalid");
        long? Usage(string name)
        {
            if (response["usage"] is null) return null;
            if (response["usage"] is not JsonObject usage || usage[name] is not JsonValue value ||
                !value.TryGetValue<long>(out var number) || number < 0) throw new ManagedProjectException("managed_response_invalid");
            return number;
        }
        var inputTokens = Usage("input_tokens");
        var outputTokens = Usage("output_tokens");
        if (inputTokens > 16000) throw new ManagedProjectException("copilot_input_token_limit");
        if (outputTokens > 2000) throw new ManagedProjectException("copilot_output_token_limit");
        return new(Identifier(response["id"]), output, inputTokens, outputTokens);
    }

    private static string Identifier(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? Identifier(text) :
            throw new ManagedProjectException("conversation_history_invalid");

    private static string Identifier(string? text) =>
        text is not null && Regex.IsMatch(text, "^[A-Za-z0-9_-]{1,128}\\z", RegexOptions.CultureInvariant) ? text :
            throw new ManagedProjectException("conversation_history_invalid");

    private static string ResultDigest(string text) => Convert.ToHexStringLower(
        System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static JsonObject ParseArguments(string arguments)
    {
        try { return JsonNode.Parse(arguments) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }
}

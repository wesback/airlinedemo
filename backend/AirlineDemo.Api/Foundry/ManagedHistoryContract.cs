using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AirlineDemo.Api.Copilot;
using AirlineDemo.Api.Evidence;
using AirlineDemo.Api.Investigation;
using AirlineDemo.Api.Persistence;

namespace AirlineDemo.Api.Foundry;

public sealed record ManagedHistoryPolicy(
    Func<string, bool> UserText,
    Func<string, bool> AssistantText,
    Func<string, JsonObject, bool> Arguments,
    Func<string, JsonObject, JsonObject, bool> ToolOutput)
{
    internal Action? Reset { get; init; }
    internal string? Workload { get; init; }
    internal Func<ManagedConversationTuple, bool>? Context { get; init; }
    internal bool AllowToolRejections { get; init; }
}

public sealed class ValidatedManagedHistory
{
    internal ValidatedManagedHistory(string json, long units)
    {
        Json = json;
        ObservableUtf16Units = units;
        Digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }

    public string Json { get; }
    public long ObservableUtf16Units { get; }
    public string Digest { get; }
    public JsonArray CopyItems() => JsonNode.Parse(Json)!.AsArray();
}

public sealed record ManagedCopilotTurnSummary(string Question, string Answer, IReadOnlyList<CopilotSourceRef> Citations);

public static class ManagedHistoryContract
{
    private static ManagedProjectException Invalid() => new("conversation_history_invalid");

    public static ManagedHistoryPolicy CopilotPolicy(string? selectedCaseId) => CreateCopilotPolicy(selectedCaseId, false);

    public static ManagedHistoryPolicy CopilotContinuationPolicy(string? selectedCaseId) => CreateCopilotPolicy(selectedCaseId, true);

    private static ManagedHistoryPolicy CreateCopilotPolicy(string? selectedCaseId, bool allowToolRejections)
    {
        var returned = new HashSet<CopilotSourceRef>();
        return new(
            text =>
            {
                returned.Clear();
                return CopilotInputContract.IsValidQuestion(text);
            },
            allowToolRejections ? CopilotQuestions.IsValidAnswerEnvelope : text => CopilotQuestions.IsValidAnswer(text, returned),
            (name, arguments) =>
            {
                if (allowToolRejections) return true;
                var (known, parsed) = CopilotMcp.Parse(name, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments.ToJsonString()), false);
                return known && parsed is not null && (name == "list_my_cases" ||
                    selectedCaseId is not null && parsed.CaseId == selectedCaseId);
            },
            (name, arguments, output) =>
            {
                if (allowToolRejections && IsToolError(output)) return true;
                var (known, parsed) = CopilotMcp.Parse(name, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments.ToJsonString()), false);
                if (!known || parsed is null || name != "list_my_cases" &&
                    (selectedCaseId is null || parsed.CaseId != selectedCaseId)) return false;
                if (name == "advance_stage" && output["error"] is null) return false;
                if (name != "list_my_cases" && output["result"] is JsonObject result &&
                    (string?)result["caseId"] != selectedCaseId) return false;
                if (name == "read_source" && (string?)arguments["operation"] != "summary" && output["result"] is JsonObject source &&
                    (string?)source["sourceId"] != (string?)arguments["sourceId"]) return false;
                var structured = (JsonObject)output.DeepClone();
                var operation = (string?)arguments["operation"];
                if (name == "read_source" && operation != "summary" && structured["error"] is null)
                {
                    structured["modelSafe"] = structured["result"]?.DeepClone();
                    structured.Remove("result");
                }
                var projected = CopilotInputContract.Observe(name, operation, structured);
                if (projected is null || !JsonNode.DeepEquals(projected, output)) return false;
                if (name == "read_source") returned.UnionWith(CopilotInputContract.ReturnedRefs(projected));
                return true;
            })
        {
            Reset = returned.Clear,
            Workload = "copilot",
            Context = tuple => selectedCaseId is null ? tuple.IsListing : !tuple.IsListing && tuple.CaseId == selectedCaseId,
            AllowToolRejections = allowToolRejections,
        };
    }

    public static ValidatedManagedHistory Validate(string json, ManagedHistoryPolicy policy, string? expectedDigest = null)
    {
        try
        {
            policy.Reset?.Invoke();
            using var document = JsonDocument.Parse(json);
            UniqueProperties(document.RootElement);
            var items = JsonNode.Parse(json) as JsonArray ?? throw Invalid();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var calls = new Dictionary<string, (string Name, JsonObject Arguments)>(StringComparer.Ordinal);
            var completed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in items)
            {
                var item = node as JsonObject ?? throw Invalid();
                if (!ids.Add(Text(item, "id"))) throw Invalid();
                switch (Text(item, "type"))
                {
                    case "message":
                    case "output_message":
                        Shape(item, "id", "type", "status", "role", "content", "phase");
                        if (Text(item, "status") != "completed") throw Invalid();
                        var role = Text(item, "role");
                        if (role is not ("user" or "assistant") || Text(item, "type") == "output_message" && role != "assistant") throw Invalid();
                        if (item.ContainsKey("phase") && Text(item, "phase") is not ("commentary" or "final_answer")) throw Invalid();
                        var content = item["content"] as JsonArray ?? throw Invalid();
                        if (content.Count == 0) throw Invalid();
                        var text = new StringBuilder();
                        foreach (var partNode in content)
                        {
                            var part = partNode as JsonObject ?? throw Invalid();
                            Shape(part, "type", "text", "annotations", "logprobs");
                            if (Text(part, "type") != (role == "user" ? "input_text" : "output_text")) throw Invalid();
                            foreach (var field in new[] { "annotations", "logprobs" })
                                if (part.ContainsKey(field) && (part[field] is not JsonArray array || array.Count != 0)) throw Invalid();
                            if (role == "user" && (part.ContainsKey("annotations") || part.ContainsKey("logprobs"))) throw Invalid();
                            text.Append(Text(part, "text"));
                        }
                        if (!(role == "user" ? policy.UserText(text.ToString()) : policy.AssistantText(text.ToString()))) throw Invalid();
                        break;
                    case "function_call":
                        Shape(item, "id", "type", "call_id", "name", "arguments", "status", "caller", "async");
                        FunctionModifiers(item);
                        var callId = Text(item, "call_id");
                        var name = Text(item, "name");
                        var arguments = policy.AllowToolRejections
                            ? ParseArguments(Text(item, "arguments"))
                            : ParseObject(Text(item, "arguments"));
                        if (!policy.Arguments(name, arguments) || !calls.TryAdd(callId, (name, arguments))) throw Invalid();
                        break;
                    case "function_call_output":
                        Shape(item, "id", "type", "call_id", "name", "output", "status", "caller");
                        FunctionModifiers(item);
                        var resultId = Text(item, "call_id");
                        if (!calls.TryGetValue(resultId, out var call) || !completed.Add(resultId) ||
                            item.ContainsKey("name") && Text(item, "name") != call.Name ||
                            !policy.ToolOutput(call.Name, call.Arguments, ParseObject(Text(item, "output")))) throw Invalid();
                        break;
                    case "reasoning":
                        Shape(item, "id", "type", "summary", "content", "encrypted_content", "status");
                        if (item["encrypted_content"] is not null) throw Invalid();
                        if (item.ContainsKey("status") && Text(item, "status") != "completed") throw Invalid();
                        Reasoning(item["summary"], "summary_text");
                        if (item.ContainsKey("content")) Reasoning(item["content"], "reasoning_text");
                        break;
                    default:
                        throw Invalid();
                }
            }
            var snapshot = new ValidatedManagedHistory(items.ToJsonString(), Units(document.RootElement));
            if (expectedDigest is not null && snapshot.Digest != expectedDigest) throw Invalid();
            return snapshot;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or ArgumentException)
        {
            throw Invalid();
        }
    }

    public static JsonObject CopilotObservation(string tool, string? operation, JsonObject structured) =>
        CopilotInputContract.Observe(tool, operation, structured) ?? throw new ManagedProjectException("copilot_context_invalid");

    public static JsonArray CopilotQuestion(string question)
    {
        if (!CopilotInputContract.IsValidQuestion(question)) throw new ManagedProjectException("invalid_question");
        return UserInput(question);
    }

    public static IReadOnlyList<ManagedCopilotTurnSummary> CopilotSummaries(string json, string selectedCaseId)
    {
        var history = Validate(json, CopilotPolicy(selectedCaseId)).CopyItems();
        var calls = history.OfType<JsonObject>().Where(item => Text(item, "type") == "function_call")
            .Select(item => Text(item, "call_id")).ToHashSet(StringComparer.Ordinal);
        var outputs = history.OfType<JsonObject>().Where(item => Text(item, "type") == "function_call_output")
            .Select(item => Text(item, "call_id")).ToHashSet(StringComparer.Ordinal);
        if (!calls.SetEquals(outputs)) throw Invalid();

        var summaries = new List<ManagedCopilotTurnSummary>();
        string? question = null;
        string? answer = null;
        IReadOnlyList<CopilotSourceRef> citations = [];
        void CompleteTurn()
        {
            if (question is null || answer is null) throw Invalid();
            summaries.Add(new(question, answer, citations));
            question = null;
            answer = null;
            citations = [];
        }

        foreach (var item in history.OfType<JsonObject>())
        {
            var type = Text(item, "type");
            if (type is "message" or "output_message")
            {
                var role = Text(item, "role");
                var content = item["content"]!.AsArray();
                var text = string.Concat(content.Select(part => Text(part!.AsObject(), "text")));
                if (role == "user")
                {
                    if (question is not null) CompleteTurn();
                    question = text;
                }
                else
                {
                    if (question is null || answer is not null) throw Invalid();
                    (answer, citations) = ParseAnswer(text);
                }
            }
            else if (question is null || answer is not null)
            {
                throw Invalid();
            }
        }

        if (question is not null) CompleteTurn();
        if (summaries.Count is < 1 or > SqlManagedConversationStore.MaximumAdmittedTurns) throw Invalid();
        return summaries;
    }

    private static (string Answer, IReadOnlyList<CopilotSourceRef> Citations) ParseAnswer(string text)
    {
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { AllowDuplicateProperties = false });
        var root = document.RootElement;
        var answer = root.GetProperty("answer").GetString() ?? throw Invalid();
        var citations = root.TryGetProperty("citations", out var list)
            ? list.EnumerateArray().Select(item => new CopilotSourceRef(
                item.GetProperty("sourceId").GetString()!, item.GetProperty("recordId").GetString()!,
                item.GetProperty("recordVersion").GetInt32())).ToArray()
            : [];
        return (answer, citations);
    }

    public static JsonArray InvestigatorDocuments(ApprovedModelManifest manifest, ModelRequest request, SyntheticContentRegistry registry)
    {
        BoundedInvestigator.ValidateDocuments(request.Evidence);
        _ = ModelCallGate.Authorize(manifest, request, registry);
        return UserInput(BoundedInvestigator.BuildPrompt(request.Evidence));
    }

    public static IReadOnlyList<InvestigationClaim> InvestigatorAnswer(string text, ApprovedModelManifest manifest,
        ModelRequest request, SyntheticContentRegistry registry, string caseId, ICaseReadSource source,
        SourceReadTools? sourceTools = null, InvestigationTraceRecorder? traceRecorder = null)
    {
        BoundedInvestigator.ValidateDocuments(request.Evidence);
        _ = ModelCallGate.Authorize(manifest, request, registry);
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { AllowDuplicateProperties = false });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array) throw BadClaim();
            foreach (var claim in root.EnumerateArray())
            {
                ClosedClaim(claim, ["text", "witnesses"], ["sourceWitnesses"]);
                if (claim.GetProperty("text").ValueKind != JsonValueKind.String ||
                    claim.GetProperty("witnesses").ValueKind != JsonValueKind.Array) throw BadClaim();
                foreach (var witness in claim.GetProperty("witnesses").EnumerateArray())
                {
                    ClosedClaim(witness, ["documentId", "page", "quote"], []);
                    if (witness.GetProperty("documentId").ValueKind != JsonValueKind.String ||
                        witness.GetProperty("quote").ValueKind != JsonValueKind.String ||
                        !PositiveInt(witness.GetProperty("page"))) throw BadClaim();
                }
                if (!claim.TryGetProperty("sourceWitnesses", out var references)) continue;
                if (references.ValueKind != JsonValueKind.Array) throw BadClaim();
                foreach (var reference in references.EnumerateArray())
                {
                    ClosedClaim(reference, ["sourceId", "recordId", "recordVersion"], []);
                    if (reference.GetProperty("sourceId").ValueKind != JsonValueKind.String ||
                        reference.GetProperty("recordId").ValueKind != JsonValueKind.String ||
                        !PositiveInt(reference.GetProperty("recordVersion"))) throw BadClaim();
                }
            }
        }
        catch (JsonException) { throw BadClaim(); }
        var evidencePages = request.Evidence
            .SelectMany(document => document.Split(EvidenceIntake.PageSeparator))
            .Select(page => page.TrimEnd('\r', '\n'))
            .ToHashSet(StringComparer.Ordinal);
        return BoundedInvestigator.VerifyClaims(text, caseId, source, traceRecorder, sourceTools, page =>
        {
            BoundedInvestigator.ValidateDocuments([page]);
            if (!evidencePages.Contains(page.TrimEnd('\r', '\n')))
                throw new ModelGateException("citation page is not part of the authorized evidence");
        });
    }

    private static InvestigationOutputException BadClaim() => new("model output is not the claim contract");

    private static bool PositiveInt(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number > 0;

    private static void ClosedClaim(JsonElement value, string[] required, string[] optional)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            required.Any(name => !value.TryGetProperty(name, out _)) ||
            value.EnumerateObject().Any(property => !required.Contains(property.Name, StringComparer.Ordinal) &&
                !optional.Contains(property.Name, StringComparer.Ordinal))) throw BadClaim();
    }

    private static JsonArray UserInput(string text) => new(new JsonObject
    {
        ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = text })
    });

    public static long EstimateInputTokens(ValidatedManagedHistory history, string instructions, string configuration,
        string toolSchemas, JsonArray pending, params string[] identifiers)
    {
        using var document = JsonDocument.Parse(pending.ToJsonString());
        var units = history.ObservableUtf16Units + instructions.Length + configuration.Length + toolSchemas.Length +
            Units(document.RootElement) + identifiers.Sum(value => (long)value.Length);
        var tokens = (units + 3) / 4;
        if (tokens > 16000) throw new ManagedProjectException("copilot_input_token_limit");
        return tokens;
    }

    private static long Units(JsonElement node) => node.ValueKind switch
    {
        JsonValueKind.Object => node.EnumerateObject().Sum(property => Units(property.Value)),
        JsonValueKind.Array => node.EnumerateArray().Sum(Units),
        JsonValueKind.String => node.GetString()!.Length,
        JsonValueKind.Null => 0,
        _ => node.GetRawText().Length
    };

    private static void UniqueProperties(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Invalid();
                UniqueProperties(property.Value);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var value in node.EnumerateArray()) UniqueProperties(value);
    }

    private static JsonObject ParseObject(string json)
    {
        using var document = JsonDocument.Parse(json);
        UniqueProperties(document.RootElement);
        return JsonNode.Parse(json) as JsonObject ?? throw Invalid();
    }

    private static JsonObject ParseArguments(string json)
    {
        try { return JsonNode.Parse(json) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }

    private static bool IsToolError(JsonObject output) =>
        output.Count == 3 && output["contractVersion"]?.GetValue<int>() == 1 &&
        output["httpStatus"] is JsonValue status && status.TryGetValue<int>(out _) &&
        output["error"] is JsonObject error && error.Count == 1 &&
        error["code"] is JsonValue code && code.TryGetValue<string>(out var value) &&
        CopilotInvocationLedger.ResultCodes.Contains(value);

    private static string Text(JsonObject node, string key) =>
        node[key] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrEmpty(text) ? text : throw Invalid();

    private static void Shape(JsonObject item, params string[] properties)
    {
        if (item.Any(property => !properties.Contains(property.Key, StringComparer.Ordinal))) throw Invalid();
    }

    private static void FunctionModifiers(JsonObject item)
    {
        if (item.ContainsKey("status") && Text(item, "status") != "completed" ||
            item["caller"] is not null && Text(item, "caller") != "direct" ||
            item.ContainsKey("async") && (item["async"] is not JsonValue value || !value.TryGetValue<bool>(out var asynchronous) || asynchronous))
            throw Invalid();
    }

    private static void Reasoning(JsonNode? node, string type)
    {
        if (node is not JsonArray parts) throw Invalid();
        foreach (var value in parts)
        {
            var part = value as JsonObject ?? throw Invalid();
            Shape(part, "type", "text");
            if (Text(part, "type") != type) throw Invalid();
            _ = Text(part, "text");
        }
    }
}

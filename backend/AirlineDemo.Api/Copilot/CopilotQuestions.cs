using System.Security.Cryptography;
using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Evaluation;
using AirlineDemo.Api.Foundry;
using AirlineDemo.Api.Investigation;
using AirlineDemo.Api.Persistence;
using Microsoft.Extensions.AI;
using AirlineDemo.Api.SourceSystems;

namespace AirlineDemo.Api.Copilot;

/// <summary>
/// The copilot Q&amp;A model client (FR-012, FR-013, FR-016): one admitted question runs through the managed agent
/// conversation and turn lifecycle. Every tool call is dispatched through the in-process MCP tool pipeline as the
/// current user; the model sees only fixed instructions, the question and rebuilt structural observations.
/// </summary>
public static class CopilotQuestions
{
    public const string Path = "/api/v1/copilot/questions";
    public const string ApprovalSetting = "Copilot:InputContractApproved";
    public const string ConfirmationAnswer = "A release proposal is waiting for your confirmation. Nothing has changed yet.";
    // Execution-local limits; the investigator's InvestigationLimits are separate and unchanged.
    public const int MaxToolAttempts = 20;
    public const int MaxModelAttempts = 8;
    public const int MaxRetries = 2;
    public const int MaxInputTokens = 16000;
    public const int MaxOutputTokens = 2000;
    public const int MaxAnswerLength = 8000;
    public const int MaxCitations = 100;
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan[] Backoff = [TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1000)];
    private static readonly AITool[] Declarations = [.. CopilotMcp.Tools.Select(tool =>
        (AITool)AIFunctionFactory.CreateDeclaration(tool.Name, tool.Description ?? "", tool.InputSchema))];
    internal static IReadOnlyList<AITool> ToolDeclarations => Declarations;

    private sealed class TurnFailure(int status, string code) : Exception
    {
        public int Status { get; } = status;
        public string Code { get; } = code;
    }

    public static IEndpointRouteBuilder MapCopilotQuestionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(Path, Ask);
        return app;
    }

    private static async Task<IResult> Ask(HttpContext http, DemoSessionStore sessions, ICopilotInvocationLedger ledger, ICaseStateStore cases)
    {
        var session = DemoAuthEndpoints.CurrentSession(http, sessions);
        if (CopilotRequestGuard.Check(http, session) is { } denied) return CopilotRequestGuard.Error(denied.Status, denied.Code);
        var body = await CopilotProposalEndpoints.ReadObject(http);
        var valid = body is not null && body.Keys.All(key => key is "question" or "selectedCaseId" or "releaseNote" or "contractVersion" or "conversationHandle")
            && body.TryGetValue("question", out var question) && question.ValueKind == JsonValueKind.String
            && OptionalString(body, "selectedCaseId") && OptionalString(body, "releaseNote") && OptionalString(body, "conversationHandle")
            && (!body.ContainsKey("contractVersion") || body["contractVersion"].ValueKind == JsonValueKind.Number
                && body["contractVersion"].TryGetInt32(out _));
        var unsupportedVersion = body is not null && body.TryGetValue("contractVersion", out var version)
            && version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out var requestedVersion)
            && requestedVersion != CopilotProjections.ContractVersion;
        var selected = valid && body!.TryGetValue("selectedCaseId", out var id) ? Text(id) : null;
        var requestedHandle = valid && body!.TryGetValue("conversationHandle", out var handle) ? Text(handle) : null;
        if (selected is null && requestedHandle is not null && SqlManagedConversationStore.IsValidHandle(requestedHandle)
            && session is not null && CopilotProjections.Roles.Contains(session.User.Role, StringComparer.Ordinal))
        {
            try
            {
                var existing = http.RequestServices.GetRequiredService<IManagedConversationStore>().ReadManagedConversation(requestedHandle);
                if (existing is { Tuple.IsListing: false } && existing.Tuple.UserId == session.User.Id
                    && session.User.CaseScopes.Contains(existing.Tuple.CaseId, StringComparer.Ordinal))
                    selected = existing.Tuple.CaseId;
            }
            catch (ManagedConversationStoreException) { }
            catch (DbException) { }
        }
        return await CopilotInvocation.RunAsync(ledger, cases, session, "question", CopilotProvenance.CopilotClient, selected,
            async (_, token) => unsupportedVersion ? Fail(400, "unsupported_contract_version")
                : valid ? await Execute(http, session!, body!, selected, token) : Fail(400, "invalid_request"), http.RequestAborted);
    }

    private static bool OptionalString(Dictionary<string, JsonElement> body, string key) =>
        !body.TryGetValue(key, out var value) || value.ValueKind == JsonValueKind.String;

    private static string? Text(JsonElement value)
    {
        try { return value.GetString(); }
        catch (InvalidOperationException) { return null; }
    }

    private static async Task<CopilotInvocationResult> Execute(HttpContext http, DemoSession session, Dictionary<string, JsonElement> body,
        string? selected, CancellationToken cancellationToken)
    {
        var question = Text(body["question"]);
        if (question is null || !CopilotInputContract.IsValidQuestion(question)) return Fail(400, "invalid_question");
        if (body.ContainsKey("selectedCaseId") && selected is null) return Fail(400, "invalid_request");
        if (body.TryGetValue("releaseNote", out var note) && Text(note) is null) return Fail(400, "invalid_request");
        if (!CopilotProjections.Roles.Contains(session.User.Role, StringComparer.Ordinal)) return Fail(403, "forbidden");
        var services = http.RequestServices;
        var projections = services.GetRequiredService<CopilotProjections>();
        if (selected is not null && projections.Authorize(session, selected).Denied is { } denied)
            return denied.Outcome == CopilotProjectionOutcome.Forbidden ? Fail(403, "forbidden") : Fail(404, "case_not_found");
        var requestedHandle = body.TryGetValue("conversationHandle", out var handleValue) ? Text(handleValue) : null;
        if (body.ContainsKey("conversationHandle") && (requestedHandle is null || !SqlManagedConversationStore.IsValidHandle(requestedHandle)))
            return Fail(400, "invalid_conversation_handle");
        var store = services.GetRequiredService<IManagedConversationStore>();
        ManagedConversationSnapshot? binding = null;
        if (requestedHandle is not null)
        {
            try { binding = store.ReadManagedConversation(requestedHandle); }
            catch (ManagedConversationStoreException failure) { return ManagedFailure(failure.Code); }
            catch (DbException) { return ManagedFailure("conversation_store_unavailable"); }
            if (binding is null || binding.Tuple.UserId != session.User.Id || binding.Tuple.IsListing)
                return ManagedFailure("conversation_not_found");
            if (services.GetRequiredService<TimeProvider>().GetUtcNow().UtcTicks >= binding.ExpiresAtTicks)
                return ManagedFailure("conversation_expired");
            if (binding.Retired || binding.RemoteConversationId is null) return ManagedFailure("conversation_unavailable");
            selected ??= binding.Tuple.CaseId;
            if (selected != binding.Tuple.CaseId) return ManagedFailure("conversation_context_mismatch");
            if (projections.Authorize(session, selected).Denied is { } resumedDenied)
                return resumedDenied.Outcome == CopilotProjectionOutcome.Forbidden ? Fail(403, "forbidden") : Fail(404, "case_not_found");
        }
        if (!(services.GetRequiredService<IConfiguration>().GetValue<bool?>(ApprovalSetting) ?? true)) return Fail(503, "copilot_input_not_approved");
        if (services.GetService<ApprovedModelManifest>() is not { } manifest || manifest.Validate().Count > 0) return Fail(503, "model_manifest_refused");
        IManagedProjectClient managed;
        ManagedAgentTarget target;
        try
        {
            managed = services.GetRequiredService<IManagedProjectClient>();
            target = managed.Agent("copilot");
        }
        catch (ManagedProjectException failure) { return ManagedFailure(failure.Code); }
        catch (InvalidOperationException) { return ManagedFailure("managed_agent_unconfigured"); }

        var sessions = services.GetRequiredService<DemoSessionStore>();
        var cases = services.GetRequiredService<ICaseStateStore>();
        ManagedConversationTuple? CurrentTuple() => BuildCurrentTuple(http, sessions, cases, projections, selected, target);
        ManagedConversationTuple? tuple;
        try { tuple = CurrentTuple(); }
        catch (ManagedProjectException failure) { return ManagedFailure(failure.Code); }
        if (tuple is null) return ManagedFailure("unauthenticated");
        if (binding is not null && tuple != binding.Tuple)
        {
            try { store.RetireManagedConversation(binding.Handle, binding.Tuple, binding.RowRevision, binding.ActiveFence); }
            catch (ManagedConversationStoreException) { }
            return ManagedFailure("conversation_context_mismatch");
        }

        http.Items[CopilotMcp.ProvenanceKey] = CopilotProvenance.CopilotClient;
        if (note.ValueKind == JsonValueKind.String) http.Items[CopilotMcp.ReleaseNoteKey] = note.GetString();
        var correlation = new GatewayCorrelation("copilot", "turn-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)),
            "managed-copilot", selected ?? "copilot-none", target.ConfigurationId, "copilot-v1", "create-conversation");
        try
        {
            var lifecycle = services.GetRequiredService<ManagedConversationLifecycle>();
            binding ??= await lifecycle.CreateAsync("copilot", tuple, correlation, CurrentTuple, cancellationToken);
            var tools = new ToolTurn(http, selected, cancellationToken, binding.Handle);
            var configuration = new ManagedTurnConfiguration(CopilotInputContract.SystemInstructions, target.ConfigurationId, ToolSchemaContract());
            var result = await services.GetRequiredService<ManagedTurnCoordinator>().RunAsync(
                "copilot", binding, CurrentTuple, correlation, configuration, ManagedHistoryContract.CopilotContinuationPolicy(selected),
                ManagedHistoryContract.CopilotQuestion(question), tools.DispatchManagedAsync, cancellationToken);
            if (result.NativeResult is { NativeReceiptCommitted: true } native)
            {
                http.Response.Headers["X-Copilot-Conversation-Handle"] = binding.Handle;
                http.Response.Headers["X-Copilot-Conversation-State"] = "retired";
                return native;
            }
            return result.NativeResult ?? tools.Answer(result.Text);
        }
        catch (ManagedProjectException failure) { return ManagedFailure(failure.Code); }
        catch (ManagedConversationStoreException failure) { return ManagedFailure(failure.Message); }
        catch (TurnFailure failure) { Console.Error.WriteLine($"copilot-turn:{failure.Code}"); return Fail(failure.Status, failure.Code); }
    }

    private static CopilotInvocationResult Fail(int status, string code) => new(CopilotRequestGuard.Error(status, code), status, code,
        status is 401 or 403 || code == "case_not_found" ? CopilotOutcome.Denied : CopilotOutcome.Failed);

    private static CopilotInvocationResult ManagedFailure(string code) => code switch
    {
        "unauthenticated" => Fail(401, code),
        "forbidden" => Fail(403, code),
        "case_not_found" or "conversation_not_found" => Fail(404, code),
        "invalid_question" => Fail(400, code),
        "conversation_busy" or "conversation_context_mismatch" => Fail(409, code),
        "conversation_expired" or "conversation_turn_limit" or "conversation_unavailable" => Fail(410, code),
        "copilot_tool_limit" or "copilot_model_limit" or "copilot_retry_limit" => Fail(429, code),
        "copilot_input_token_limit" or "copilot_output_token_limit" or "managed_content_filtered" or "managed_refusal" => Fail(422, code),
        "copilot_timeout" => Fail(504, code),
        "managed_agent_unconfigured" or "model_unavailable" or "conversation_not_ready" => Fail(503, "managed_agent_unconfigured"),
        "conversation_store_unavailable" => Fail(503, code),
        "managed_operation_ambiguous" or "conversation_history_invalid" or "managed_response_invalid" or "copilot_context_invalid" =>
            Fail(502, code is "managed_response_invalid" or "conversation_history_invalid" ? "model_response_invalid" : code),
        _ => Fail(502, "model_response_invalid"),
    };

    internal static ManagedConversationTuple? BuildCurrentTuple(HttpContext http, DemoSessionStore sessions, ICaseStateStore cases,
        CopilotProjections projections, string? selected, ManagedAgentTarget target)
    {
        var session = DemoAuthEndpoints.CurrentSession(http, sessions);
        if (session is null) throw new ManagedProjectException("unauthenticated");
        if (!CopilotProjections.Roles.Contains(session.User.Role, StringComparer.Ordinal))
            throw new ManagedProjectException("forbidden");
        var fingerprint = EntitlementFingerprint(session.User);
        if (selected is null)
            return new ManagedConversationTuple(session.User.Id, session.User.Role, "copilot-none", fingerprint, 0,
                target.Name, target.Version, target.ConfigurationId, IsListing: true);

        var access = projections.Authorize(session, selected);
        if (access.Denied is { } denied)
            throw new ManagedProjectException(denied.Outcome switch
            {
                CopilotProjectionOutcome.Unauthenticated => "unauthenticated",
                CopilotProjectionOutcome.Forbidden => "forbidden",
                _ => "case_not_found",
            });
        var scope = access.Scope ?? throw new ManagedProjectException("case_not_found");
        return new ManagedConversationTuple(session.User.Id, session.User.Role, selected, fingerprint,
            cases.Read(scope).Revision, target.Name, target.Version, target.ConfigurationId);
    }

    internal static string EntitlementFingerprint(DemoUser user)
    {
        var permissions = new
        {
            Role = user.Role,
            Cases = user.CaseScopes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            Sources = SourceModes.Sources.Where(source => SourceReadEndpoints.Allowed(user.Role, source))
                .Order(StringComparer.Ordinal).ToArray(),
        };
        return Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(permissions))));
    }

    private static string ToolSchemaContract() => JsonSerializer.Serialize(Declarations.OfType<AIFunctionDeclaration>()
        .Select(tool => new { tool.Name, tool.Description, Schema = tool.JsonSchema.GetRawText() }));

    internal static bool IsValidAnswer(string text, IReadOnlySet<CopilotSourceRef> returned) => ToolTurn.IsValidAnswer(text, returned);
    internal static bool IsValidAnswerEnvelope(string text) => ToolTurn.IsValidAnswerEnvelope(text);

    internal sealed class ToolTurn(HttpContext http, string? selected, CancellationToken token, string? conversationHandle = null)
    {
        private static readonly JsonSerializerOptions EscapedJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default };
        private readonly JsonArray _records = [];
        private readonly HashSet<string> _recordIdentities = new(StringComparer.Ordinal);
        // Only source/record/version tuples read_source returned in this turn are citable.
        private readonly HashSet<CopilotSourceRef> _returned = [];

        internal async Task<JsonObject> DispatchManagedAsync(ManagedToolSelection selection, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            http.Items[CopilotMcp.ProvenanceKey] = CopilotProvenance.CopilotClient;
            var rawArguments = selection.RawArguments ?? selection.Arguments.ToJsonString();
            Dictionary<string, object?>? arguments;
            try { arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(rawArguments); }
            catch (JsonException) { arguments = null; }
            var call = new FunctionCallContent(selection.CallId, selection.Name, arguments)
            {
                RawRepresentation = OpenAI.Chat.ChatToolCall.CreateFunctionToolCall(
                    selection.CallId, selection.Name, BinaryData.FromString(rawArguments)),
            };
            try
            {
                var (_, _, observation, terminal) = await DispatchAsync(call, cancellationToken);
                if (terminal is not null) throw new ManagedToolTerminal(terminal, terminal.NativeReceiptCommitted);
                return observation;
            }
            catch (TurnFailure failure) { throw new ManagedProjectException(failure.Code); }
        }

        // A call refused before dispatch is still one ledger invocation, recorded against the trusted selected case only;
        // the model's other case ID is never passed to the ledger.
        private async Task<JsonObject> RefuseAsync(string operation, int status, string code, CancellationToken cancellationToken)
        {
            var services = http.RequestServices;
            var session = DemoAuthEndpoints.CurrentSession(http, services.GetRequiredService<DemoSessionStore>());
            var result = await CopilotInvocation.RunAsync(services.GetRequiredService<ICopilotInvocationLedger>(),
                services.GetRequiredService<ICaseStateStore>(), session, operation, CopilotProvenance.CopilotClient, selected,
                _ => Task.FromResult(Fail(status, code)), cancellationToken);
            if (result is IStatusCodeHttpResult { StatusCode: StatusCodes.Status503ServiceUnavailable }) throw new TurnFailure(503, "audit_unavailable");
            return Error(status, code);
        }

        internal async Task<(string Name, Dictionary<string, object?> Arguments, JsonObject Observation, CopilotInvocationResult? Terminal)> DispatchAsync(
            FunctionCallContent call, CancellationToken? executionToken = null)
        {
            var callToken = executionToken ?? token;
            callToken.ThrowIfCancellationRequested();
            var (arguments, reject) = RawArguments(call);
            var (known, args) = CopilotMcp.Parse(call.Name, arguments, reject);
            if (!known) return ("unknown_tool", [], await RefuseAsync("unknown_tool", 400, "unknown_tool", callToken), null);
            var retained = args is null ? new Dictionary<string, object?>() : Retained(args);
            if (args is not null && call.Name != "list_my_cases")
            {
                if (selected is null) return (call.Name, retained, await RefuseAsync(call.Name, 409, "case_selection_required", callToken), null);
                if (args.CaseId != selected) return (call.Name, retained, await RefuseAsync(call.Name, 403, "selected_case_mismatch", callToken), null);
            }
            var tool = await CopilotMcp.InvokeToolAsync(http, call.Name, arguments, reject, callToken);
            // A read the server withheld for an invalid record identity or a lost audit stops the turn, as the observation contract would.
            if (tool.Structured["error"]?["code"] is JsonValue failed && (string?)failed is "copilot_context_invalid" or "audit_unavailable")
                throw new TurnFailure((int)tool.Structured["httpStatus"]!, (string)failed!);
            if (tool.Structured["resultCode"] is JsonValue code && (string?)code == "confirmation_required")
                return (call.Name, retained, tool.Structured, Result(ConfirmationAnswer, "confirmation_required", tool.Structured["uiOnly"]!.DeepClone()));
            if (tool.Raw is not null)
                return (call.Name, retained, tool.Structured, new CopilotInvocationResult(
                    Results.Text(tool.Raw, "application/json; charset=utf-8"), tool.HttpStatus, "ok", CopilotOutcome.Succeeded)
                    { NativeReceiptCommitted = true });
            var observation = CopilotInputContract.Observe(call.Name, args?.Operation, tool.Structured)
                ?? throw new TurnFailure(502, "copilot_context_invalid");
            _returned.UnionWith(CopilotInputContract.ReturnedRefs(observation));
            if (tool.Structured["untrustedRecords"] is JsonObject envelope)
            {
                if (envelope["records"] is JsonArray list) foreach (var record in list) AddRecord(record);
                else AddRecord(envelope["record"]);
            }
            return (call.Name, retained, observation, null);
        }

        // A record read twice in one turn (a list, then the record) is shown once: one entry per source, record and version.
        private void AddRecord(JsonNode? record)
        {
            var identity = $"{(string?)record?["sourceId"]}\n{(string?)record?["recordId"]}\n{(int?)record?["recordVersion"]}";
            if (_recordIdentities.Add(identity)) _records.Add(record?.DeepClone());
        }

        // Duplicate or non-object argument JSON is refused by the tool; the raw OpenAI arguments are read when present.
        private static (Dictionary<string, JsonElement> Arguments, bool Reject) RawArguments(FunctionCallContent call)
        {
            var arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (call.Exception is not null) return (arguments, true);
            var raw = call.RawRepresentation is OpenAI.Chat.ChatToolCall tool ? tool.FunctionArguments.ToString()
                : JsonSerializer.Serialize(call.Arguments ?? new Dictionary<string, object?>());
            try
            {
                using var document = JsonDocument.Parse(raw);
                if (document.RootElement.ValueKind != JsonValueKind.Object) return (arguments, true);
                foreach (var property in document.RootElement.EnumerateObject())
                    if (!arguments.TryAdd(property.Name, property.Value.Clone())) return (arguments, true);
                return (arguments, false);
            }
            catch (JsonException) { return (arguments, true); }
        }

        private static Dictionary<string, object?> Retained(CopilotMcp.ToolArgs args)
        {
            var retained = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (args.CaseId is not null) retained["caseId"] = args.CaseId;
            if (args.SourceId is not null) retained["sourceId"] = args.SourceId;
            if (args.Operation is not null) retained["operation"] = args.Operation;
            if (args.RecordId is not null) retained["recordId"] = args.RecordId;
            if (args.ExpectedRevision is not null) retained["expectedRevision"] = args.ExpectedRevision;
            if (args.ProposalId is not null) retained["proposalId"] = args.ProposalId;
            return retained;
        }

        private static JsonObject Error(int status, string code) => new()
        {
            ["contractVersion"] = CopilotProjections.ContractVersion, ["httpStatus"] = status, ["error"] = new JsonObject { ["code"] = code },
        };

        // The final answer is {"answer": "<plain text>", "citations"?: [tuple]}; any other shape is an invalid model response.
        internal CopilotInvocationResult Answer(string text)
        {
            var (answer, citations) = ParseAnswer(text, _returned);
            return Result(answer, "ok", null, citations);
        }

        internal static bool IsValidAnswer(string text, IReadOnlySet<CopilotSourceRef> returned)
        {
            try { _ = ParseAnswer(text, returned); return true; }
            catch (TurnFailure) { return false; }
        }

        internal static bool IsValidAnswerEnvelope(string text)
        {
            try
            {
                var root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowDuplicateProperties = false }) as JsonObject;
                return root is not null && root.All(property => property.Key is "answer" or "citations")
                    && root["answer"] is JsonValue answer && answer.GetValueKind() == JsonValueKind.String
                    && answer.GetValue<string>() is { Length: > 0 and <= MaxAnswerLength } value && !string.IsNullOrWhiteSpace(value);
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException) { return false; }
        }

        private static (string Answer, List<CopilotSourceRef> Citations) ParseAnswer(string text, IReadOnlySet<CopilotSourceRef> returned)
        {
            JsonObject? root;
            try { root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowDuplicateProperties = false }) as JsonObject; }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException) { root = null; }
            if (root is null || root.Any(property => property.Key is not ("answer" or "citations"))
                || root["answer"] is not JsonValue answer || answer.GetValueKind() != JsonValueKind.String
                || answer.GetValue<string>() is not { Length: > 0 and <= MaxAnswerLength } value || string.IsNullOrWhiteSpace(value))
                throw new TurnFailure(502, "model_response_invalid");
            return (value, root.ContainsKey("citations") ? Citations(root["citations"], returned) : []);
        }

        // A citation is exactly one tuple read_source returned in this turn; a URL, point reference, other record,
        // version or case is refused and no link is produced.
        private static List<CopilotSourceRef> Citations(JsonNode? node, IReadOnlySet<CopilotSourceRef> returned)
        {
            var cited = new List<CopilotSourceRef>();
            if (node is not JsonArray array || array.Count > MaxCitations) throw new TurnFailure(502, "citation_invalid");
            foreach (var item in array)
            {
                if (item is not JsonObject citation || citation.Count != 3 || citation["sourceId"] is not JsonValue source
                    || source.GetValueKind() != JsonValueKind.String || citation["recordId"] is not JsonValue record
                    || record.GetValueKind() != JsonValueKind.String || citation["recordVersion"] is not JsonValue version
                    || version.GetValueKind() != JsonValueKind.Number || !version.TryGetValue<int>(out var number))
                    throw new TurnFailure(502, "citation_invalid");
                var reference = new CopilotSourceRef(source.GetValue<string>(), record.GetValue<string>(), number);
                if (!returned.Contains(reference)) throw new TurnFailure(502, "citation_invalid");
                if (!cited.Contains(reference)) cited.Add(reference);
            }
            return cited;
        }

        private CopilotInvocationResult Result(string answer, string code, JsonNode? confirmation, List<CopilotSourceRef>? citations = null)
        {
            var body = new JsonObject
            {
                ["contractVersion"] = CopilotProjections.ContractVersion, ["answer"] = answer,
                ["citations"] = new JsonArray([.. (citations ?? []).Select(item => (JsonNode)new JsonObject
                {
                    ["sourceId"] = item.SourceId, ["recordId"] = item.RecordId, ["recordVersion"] = item.RecordVersion,
                })]),
                ["records"] = _records,
            };
            if (conversationHandle is not null)
            {
                body["conversationHandle"] = conversationHandle;
                body["conversationState"] = selected is null ? "retired" : "active";
            }
            if (confirmation is not null) body["confirmation"] = confirmation;
            // The minimal-API default encoder is relaxed; untrusted record text is written with HTML-sensitive characters escaped.
            return new(Results.Text(body.ToJsonString(EscapedJson), "application/json; charset=utf-8"), 200, code, CopilotOutcome.Succeeded);
        }
    }
}

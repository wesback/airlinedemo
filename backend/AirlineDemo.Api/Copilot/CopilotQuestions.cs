using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Evaluation;
using AirlineDemo.Api.Investigation;
using AirlineDemo.Api.Persistence;
using Microsoft.Extensions.AI;

namespace AirlineDemo.Api.Copilot;

/// <summary>
/// The copilot Q&amp;A model client (FR-012, FR-013, FR-016): one admitted question runs a bounded model/tool loop through
/// the governed gateway client. Every tool call is dispatched through the in-process MCP tool pipeline as the current
/// user; the model sees only fixed instructions, the question and rebuilt structural observations.
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
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan[] Backoff = [TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1000)];
    private static readonly Regex CallId = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly AITool[] Declarations = [.. CopilotMcp.Tools.Select(tool =>
        (AITool)AIFunctionFactory.CreateDeclaration(tool.Name, tool.Description ?? "", tool.InputSchema))];

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
        var valid = body is not null && body.Keys.All(key => key is "question" or "selectedCaseId" or "releaseNote") && body.ContainsKey("question")
            && body.Values.All(value => value.ValueKind == JsonValueKind.String);
        var selected = valid && body!.TryGetValue("selectedCaseId", out var id) ? Text(id) : null;
        return await CopilotInvocation.RunAsync(ledger, cases, session, "question", CopilotProvenance.CopilotClient, selected,
            async (_, token) => valid ? await Execute(http, session!, body!, selected, token) : Fail(400, "invalid_request"), http.RequestAborted);
    }

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
        if (selected is not null && services.GetRequiredService<CopilotProjections>().Authorize(session, selected).Denied is { } denied)
            return denied.Outcome == CopilotProjectionOutcome.Forbidden ? Fail(403, "forbidden") : Fail(404, "case_not_found");
        if (!(services.GetRequiredService<IConfiguration>().GetValue<bool?>(ApprovalSetting) ?? true)) return Fail(503, "copilot_input_not_approved");
        if (services.GetService<ApprovedModelManifest>() is not { } manifest || manifest.Validate().Count > 0) return Fail(503, "model_manifest_refused");
        IChatClient client;
        try { client = services.GetRequiredService<IChatClient>(); }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or UriFormatException)
        {
            return Fail(503, "model_unavailable");
        }

        http.Items[CopilotMcp.ProvenanceKey] = CopilotProvenance.CopilotClient;
        if (note.ValueKind == JsonValueKind.String) http.Items[CopilotMcp.ReleaseNoteKey] = note.GetString();
        var time = services.GetRequiredService<TimeProvider>();
        using var deadline = new CancellationTokenSource(Deadline, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var turn = new Turn(http, client, manifest, time, question, selected, linked.Token);
        try { return await turn.RunAsync(); }
        catch (TurnFailure failure) { return Fail(failure.Status, failure.Code); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return Fail(504, "copilot_timeout");
        }
    }

    private static CopilotInvocationResult Fail(int status, string code) => new(CopilotRequestGuard.Error(status, code), status, code,
        status is 401 or 403 || code == "case_not_found" ? CopilotOutcome.Denied : CopilotOutcome.Failed);

    private sealed class Turn(HttpContext http, IChatClient client, ApprovedModelManifest manifest, TimeProvider time, string question,
        string? selected, CancellationToken token)
    {
        private readonly DateTimeOffset _start = time.GetUtcNow();
        private readonly string _run = "turn-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        private readonly List<ChatMessage> _history = [];
        private readonly HashSet<string> _callIds = new(StringComparer.Ordinal);
        private readonly JsonArray _records = [];
        private int _modelAttempts;
        private int _toolAttempts;

        public async Task<CopilotInvocationResult> RunAsync()
        {
            while (true)
            {
                var response = await CallModelAsync();
                var calls = response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>().ToArray();
                if (calls.Length == 0) return Answer(response.Text);
                if (calls.Any(call => call.CallId is null || !CallId.IsMatch(call.CallId) || !_callIds.Add(call.CallId)))
                    throw new TurnFailure(502, "model_response_invalid");
                var requested = new List<AIContent>();
                var observed = new List<AIContent>();
                foreach (var call in calls)
                {
                    if (++_toolAttempts > MaxToolAttempts) throw new TurnFailure(429, "copilot_tool_limit");
                    var (name, arguments, observation, confirmation) = await DispatchAsync(call);
                    if (confirmation is not null) return Result(ConfirmationAnswer, "confirmation_required", confirmation);
                    requested.Add(new FunctionCallContent(call.CallId, name, arguments));
                    observed.Add(new FunctionResultContent(call.CallId, observation.ToJsonString()));
                }
                // Only validated tool-call metadata is retained; the model's intermediate text is never fed back.
                _history.Add(new ChatMessage(ChatRole.Assistant, requested));
                _history.Add(new ChatMessage(ChatRole.Tool, observed));
            }
        }

        private async Task<ChatResponse> CallModelAsync()
        {
            ChatMessage[] messages = [new(ChatRole.System, CopilotInputContract.SystemInstructions), new(ChatRole.User, question), .. _history];
            if (CopilotInputContract.EstimateInputTokens(messages, Declarations) > MaxInputTokens) throw new TurnFailure(422, "copilot_input_token_limit");
            var options = new ChatOptions { Tools = [.. Declarations], MaxOutputTokens = MaxOutputTokens, AllowMultipleToolCalls = true };
            for (var retries = 0; ; retries++)
            {
                if (++_modelAttempts > MaxModelAttempts) throw new TurnFailure(429, "copilot_model_limit");
                CheckDeadline();
                var correlation = new GatewayCorrelation("copilot", _run, "copilot-questions", selected ?? "copilot-none",
                    $"{manifest.DeploymentName}-{manifest.Model}-{manifest.ApiVersion}-{manifest.Region}", "copilot-v1", $"call-{_modelAttempts}");
                ChatResponse response;
                try
                {
                    ModelGatewayTokenomics.ValidateCorrelation(correlation);
                    using var scope = GatewayCorrelationContext.Begin(correlation);
                    response = await client.GetResponseAsync(messages, options, token);
                }
                catch (Exception exception) when (IsTransient(exception))
                {
                    if (retries == MaxRetries) throw new TurnFailure(429, "copilot_retry_limit");
                    await Task.Delay(Backoff[retries], time, token);
                    continue;
                }
                catch (Exception exception) when (exception is not OperationCanceledException and not TurnFailure)
                {
                    throw new TurnFailure(503, "model_unavailable");
                }
                CheckDeadline();
                if (response.Usage?.InputTokenCount > MaxInputTokens) throw new TurnFailure(422, "copilot_input_token_limit");
                if (response.Usage?.OutputTokenCount > MaxOutputTokens || response.FinishReason == ChatFinishReason.Length)
                    throw new TurnFailure(422, "copilot_output_token_limit");
                return response;
            }
        }

        private void CheckDeadline()
        {
            token.ThrowIfCancellationRequested();
            if (time.GetUtcNow() - _start >= Deadline) throw new TurnFailure(504, "copilot_timeout");
        }

        // The same transient classification as the investigator's gated client.
        private static bool IsTransient(Exception exception) =>
            exception is HttpRequestException or TimeoutException
            || exception is System.ClientModel.ClientResultException { Status: 408 or 429 }
            || exception is System.ClientModel.ClientResultException { Status: >= 500 and <= 599 };

        private async Task<(string Name, Dictionary<string, object?> Arguments, JsonObject Observation, JsonNode? Confirmation)> DispatchAsync(
            FunctionCallContent call)
        {
            var (arguments, reject) = RawArguments(call);
            var (known, args) = CopilotMcp.Parse(call.Name, arguments, reject);
            if (!known) return ("unknown_tool", [], Error(400, "unknown_tool"), null);
            var retained = args is null ? new Dictionary<string, object?>() : Retained(args);
            if (args is not null && call.Name != "list_my_cases")
            {
                if (selected is null) return (call.Name, retained, Error(409, "case_selection_required"), null);
                if (args.CaseId != selected) return (call.Name, retained, Error(403, "selected_case_mismatch"), null);
            }
            var tool = await CopilotMcp.InvokeToolAsync(http, call.Name, arguments, reject, token);
            if (tool.Structured["resultCode"] is JsonValue code && (string?)code == "confirmation_required")
                return (call.Name, retained, tool.Structured, tool.Structured["uiOnly"]!.DeepClone());
            var observation = CopilotInputContract.Observe(call.Name, args?.Operation, tool.Structured)
                ?? throw new TurnFailure(502, "copilot_context_invalid");
            if (tool.Structured["untrustedRecords"] is JsonObject envelope)
            {
                if (envelope["records"] is JsonArray list) foreach (var record in list) _records.Add(record?.DeepClone());
                else _records.Add(envelope["record"]?.DeepClone());
            }
            return (call.Name, retained, observation, null);
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

        // The final answer is exactly {"answer": "<plain text>"}; anything else is an invalid model response.
        private CopilotInvocationResult Answer(string text)
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.EnumerateObject().Count() == 1 && root.TryGetProperty("answer", out var answer)
                    && answer.ValueKind == JsonValueKind.String && answer.GetString() is { Length: > 0 and <= MaxAnswerLength } value
                    && !string.IsNullOrWhiteSpace(value))
                    return Result(value, "ok", null);
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException) { }
            throw new TurnFailure(502, "model_response_invalid");
        }

        private CopilotInvocationResult Result(string answer, string code, JsonNode? confirmation)
        {
            var body = new JsonObject
            {
                ["contractVersion"] = CopilotProjections.ContractVersion, ["answer"] = answer, ["citations"] = new JsonArray(), ["records"] = _records,
            };
            if (confirmation is not null) body["confirmation"] = confirmation;
            return new(Results.Json(body), 200, code, CopilotOutcome.Succeeded);
        }
    }
}

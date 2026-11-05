using System.Net;
using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Copilot;
using AirlineDemo.Api.Evaluation;
using AirlineDemo.Api.Foundry;
using AirlineDemo.Api.Decisions;
using AirlineDemo.Api.Finance;
using AirlineDemo.Api.Investigation;
using AirlineDemo.Api.SourceSystems;
using AirlineDemo.Api.Persistence;
using Azure.Core;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AirlineDemo.Api.Tests;

public sealed class ManagedAgentRuntimeTests
{
    private const string Digest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Answer = "{\"answer\":\"Ready to review.\",\"citations\":[]}";
    private static ManagedConversationTuple Tuple() => new("owner", "presenter", "cp-case-a", "entitlements", 0, "airline-copilot", "1", Digest);
    private static GatewayCorrelation Correlation() => new("copilot", "run-1", "managed", "cp-case-a", Digest, "v1", "call-1");

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, "name")]
    [InlineData(false, "version")]
    [InlineData(false, "type")]
    [InlineData(false, "response")]
    [InlineData(false, "extra")]
    [InlineData(true, "name")]
    [InlineData(true, "version")]
    [InlineData(true, "type")]
    [InlineData(true, "extra")]
    [InlineData(true, "partition")]
    public async Task ProductionHost_NativeItemMetadata_BindsExactAgentAndResponse(bool history, string? fault)
    {
        await using var harness = new Harness();
        await harness.InitializeAsync();
        var binding = await harness.CreateAsync();
        JsonObject Native(JsonObject item, bool stored)
        {
            var agent = new JsonObject
            {
                ["type"] = stored ? "agent_id" : "agent_reference",
                ["name"] = "airline-copilot",
                ["version"] = "1"
            };
            if (fault is "name" or "version" or "type") agent[fault] = "different";
            if (fault == "extra") agent["instructions"] = "UNTRUSTED";
            if (stored)
            {
                item["created_by"] = new JsonObject { ["response_id"] = "resp-1", ["agent"] = agent };
                item["partition_key"] = fault == "partition" ? "invalid/key" : "partition-1";
            }
            else
            {
                item["agent_reference"] = agent;
                item["response_id"] = fault == "response" ? "resp-other" : "resp-2";
            }
            return item;
        }
        harness.Edge.Respond = request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                var items = new JsonArray();
                if (history)
                {
                    items.Add(new JsonObject
                    {
                        ["id"] = "question-1",
                        ["type"] = "message",
                        ["status"] = "completed",
                        ["role"] = "user",
                        ["partition_key"] = "partition-1",
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = "What should I review?" })
                    });
                    items.Add(Native(Message("previous-answer", Answer), true));
                }
                return new JsonObject { ["data"] = items, ["has_more"] = false };
            }
            var message = Message("answer-1", Answer);
            return Response(new JsonArray(history ? message : Native(message, false)));
        };
        var operation = harness.RunAsync(binding, (_, _) => throw new InvalidOperationException("No tool is requested"));
        if (fault is null)
        {
            Assert.Equal(Answer, (await operation).Text);
            Assert.False(harness.Store.ReadManagedConversation(binding.Handle)!.Retired);
            Assert.Equal(1, harness.Edge.Submissions);
        }
        else
        {
            Assert.Equal("conversation_history_invalid", (await Assert.ThrowsAsync<ManagedProjectException>(() => operation)).Code);
            Assert.True(harness.Store.ReadManagedConversation(binding.Handle)!.Retired);
            Assert.Equal(history ? 0 : 1, harness.Edge.Submissions);
        }
        Assert.Equal(1, harness.Edge.Reads);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("native", null)]
    [InlineData("history", "conversation_history_invalid")]
    [InlineData("incomplete-history", "conversation_history_invalid")]
    [InlineData("unknown-call", "conversation_history_invalid")]
    [InlineData("duplicate-call", "conversation_history_invalid")]
    [InlineData("context-after-response", "conversation_context_mismatch")]
    [InlineData("context-after-tool", "conversation_context_mismatch")]
    [InlineData("unsafe-tool", "conversation_history_invalid")]
    [InlineData("lost-write", "managed_operation_ambiguous")]
    [InlineData("refusal", "managed_refusal")]
    [InlineData("filtered", "managed_content_filtered")]
    [InlineData("output-limit", "copilot_output_token_limit")]
    [InlineData("usage-boundary", null)]
    [InlineData("usage-limit", "copilot_output_token_limit")]
    [InlineData("target", "managed_response_invalid")]
    [InlineData("configuration", "managed_response_invalid")]
    public async Task ProductionHost_ManagedContinuation_ValidatesBeforeDispatchAndCompletion(string? fault, string? expectedCode)
    {
        await using var harness = new Harness();
        await harness.InitializeAsync();
        var binding = await harness.CreateAsync();
        harness.Edge.Respond = request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                var data = new JsonArray();
                if (fault == "history") data.Add(new JsonObject { ["id"] = "opaque-1", ["type"] = "unknown" });
                return new JsonObject { ["data"] = data, ["has_more"] = fault == "incomplete-history" };
            }
            if (fault == "lost-write") throw new HttpRequestException("PRIVATE_PROVIDER_DETAIL");
            if (harness.Edge.Submissions == 1)
            {
                var output = new JsonArray(Call("fc-1", "call-1", fault == "unknown-call" ? "unregistered" : "list_my_cases"));
                if (fault == "duplicate-call") output.Add(Call("fc-2", "call-1", "list_my_cases"));
                var response = Response(output);
                if (fault == "native")
                    foreach (var item in output.OfType<JsonObject>())
                    {
                        item["agent_reference"] = response["agent_reference"]!.DeepClone();
                        item["response_id"] = "resp-2";
                    }
                if (fault == "context-after-response") harness.Current = Tuple() with { CaseRevision = 1 };
                if (fault == "refusal") output.Add(new JsonObject
                {
                    ["id"] = "refusal-1", ["type"] = "output_message", ["status"] = "completed", ["role"] = "assistant",
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "refusal", ["refusal"] = "PRIVATE_REFUSAL" })
                });
                if (fault is "filtered" or "output-limit") response["incomplete_details"] = new JsonObject
                {
                    ["reason"] = fault == "filtered" ? "content_filter" : "max_output_tokens"
                };
                if (fault is "usage-boundary" or "usage-limit")
                    response["usage"] = new JsonObject { ["input_tokens"] = 1, ["output_tokens"] = fault == "usage-boundary" ? 2000 : 2001 };
                if (fault == "target") response["agent_reference"]!["version"] = "2";
                if (fault == "configuration") response["max_output_tokens"] = 2001;
                return response;
            }
            var finalResponse = Response(new JsonArray(Message("answer-1", Answer)));
            if (fault == "usage-boundary")
                finalResponse["usage"] = new JsonObject { ["input_tokens"] = 1, ["output_tokens"] = 2000 };
            return finalResponse;
        };
        var dispatches = 0;
        var operation = harness.RunAsync(binding, (_, _) =>
        {
            dispatches++;
            if (fault == "context-after-tool") harness.Current = Tuple() with { EntitlementFingerprint = "changed" };
            var observation = new JsonObject
            {
                ["contractVersion"] = 1, ["httpStatus"] = 200,
                ["result"] = new JsonObject { ["contractVersion"] = 1, ["cases"] = new JsonArray() }
            };
            if (fault == "unsafe-tool") observation["rawSource"] = "RAW_SENTINEL";
            return Task.FromResult(observation);
        });
        if (expectedCode is null)
        {
            var result = await operation;
            Assert.Equal(Answer, result.Text);
            Assert.Equal("resp-2", result.ResponseId);
            Assert.Equal(["answer-1"], result.ItemIds);
            Assert.Equal(fault == "usage-boundary" ? 1 : null, result.InputTokens);
            Assert.Equal(fault == "usage-boundary" ? 2000 : null, result.OutputTokens);
            Assert.Equal(1, dispatches);
            Assert.Equal(2, harness.Edge.Submissions);
            Assert.Equal(new JsonObject
            {
                ["type"] = "function_call_output", ["call_id"] = "call-1",
                ["output"] = "{\"contractVersion\":1,\"httpStatus\":200,\"result\":{\"contractVersion\":1,\"cases\":[]}}"
            }.ToJsonString(), harness.Edge.Inputs[1]![0]!.ToJsonString());
        }
        else
        {
            Assert.Equal(expectedCode, (await Assert.ThrowsAsync<ManagedProjectException>(() => operation)).Code);
            Assert.True(harness.Store.ReadManagedConversation(binding.Handle)!.Retired);
            Assert.Equal(fault is "context-after-tool" or "unsafe-tool" ? 1 : 0, dispatches);
            Assert.Equal(fault is "history" or "incomplete-history" ? 0 : 1, harness.Edge.Submissions);
            if (fault is "filtered" or "refusal")
            {
                Assert.All(harness.Edge.Inputs, input =>
                    Assert.DoesNotContain(Assert.IsType<JsonArray>(input).OfType<JsonObject>(),
                        item => (string?)item["type"] == "function_call_output"));
            }
        }
    }

    [Theory]
    [InlineData(20, 20, 2, null, false)]
    [InlineData(21, 0, 1, "copilot_tool_limit", false)]
    [InlineData(8, 8, 8, "copilot_model_limit", false)]
    [InlineData(8, 7, 8, null, true)]
    public async Task ProductionHost_ManagedLimits_ToolAndSubmissionBounds(int selections, int expectedDispatches, int expectedSubmissions, string? code, bool finalAtLimit)
    {
        await using var harness = new Harness();
        await harness.InitializeAsync();
        var binding = await harness.CreateAsync();
        harness.Edge.Respond = request =>
        {
            if (request.Method == HttpMethod.Get) return new JsonObject { ["data"] = new JsonArray(), ["has_more"] = false };
            if (harness.Edge.Submissions > 1 && (selections != 8 || finalAtLimit && harness.Edge.Submissions == 8))
                return Response(new JsonArray(Message("answer-1", Answer)));
            return Response(new JsonArray([.. Enumerable.Range(1, selections == 8 ? 1 : selections).Select(index =>
                (JsonNode)Call($"fc-{harness.Edge.Submissions}-{index}", $"call-{harness.Edge.Submissions}-{index}", "list_my_cases"))]));
        };
        var count = 0;
        var operation = harness.RunAsync(binding, (_, _) =>
        {
            count++;
            return Task.FromResult(new JsonObject
            {
                ["contractVersion"] = 1, ["httpStatus"] = 200,
                ["result"] = new JsonObject { ["contractVersion"] = 1, ["cases"] = new JsonArray() }
            });
        });
        if (code is null) Assert.Equal(Answer, (await operation).Text);
        else Assert.Equal(code, (await Assert.ThrowsAsync<ManagedProjectException>(() => operation)).Code);
        Assert.Equal(expectedDispatches, count);
        Assert.Equal(expectedSubmissions, harness.Edge.Submissions);
    }

    [Theory]
    [InlineData(2, 200, 3, 1, null)]
    [InlineData(3, 200, 3, 0, "copilot_retry_limit")]
    [InlineData(0, 403, 1, 0, "model_unavailable")]
    [InlineData(0, 404, 1, 0, "conversation_unavailable")]
    public async Task ProductionHost_ManagedReads_RetryOnlyProvenTransientReads(int failures, int status, int reads, int submissions, string? code)
    {
        await using var harness = new Harness();
        await harness.InitializeAsync();
        var binding = await harness.CreateAsync();
        harness.Edge.ReadFailures = failures;
        harness.Edge.ReadStatus = (HttpStatusCode)status;
        harness.Edge.Respond = request => request.Method == HttpMethod.Get
            ? new JsonObject { ["data"] = new JsonArray(), ["has_more"] = false }
            : Response(new JsonArray(Message("answer-1", Answer)));
        var dispatches = 0;
        var operation = harness.RunAsync(binding, (_, _) =>
        {
            dispatches++;
            return Task.FromResult(new JsonObject());
        });
        if (code is null) Assert.Equal(Answer, (await operation).Text);
        else Assert.Equal(code, (await Assert.ThrowsAsync<ManagedProjectException>(() => operation)).Code);
        Assert.Equal(0, dispatches);
        Assert.Equal(reads, harness.Edge.Reads);
        Assert.Equal(submissions, harness.Edge.Submissions);
        Assert.Equal(failures == 0 ? [] : new[] { TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1000) }, harness.Time.Delays);
    }

    [Fact]
    public async Task ProductionHost_ManagedHistory_PaginatesBeforeOneSubmissionAndRetiresOnRepeatedId()
    {
        await using var harness = new Harness();
        await harness.InitializeAsync();
        var binding = await harness.CreateAsync();
        harness.Edge.Respond = request => request.Method == HttpMethod.Get
            ? new JsonObject
            {
                ["data"] = new JsonArray(new JsonObject { ["id"] = "reason-1", ["type"] = "reasoning", ["summary"] = new JsonArray() }),
                ["has_more"] = harness.Edge.Reads == 1
            }
            : Response(new JsonArray(Message("answer-1", Answer)));
        Assert.Equal("conversation_history_invalid", (await Assert.ThrowsAsync<ManagedProjectException>(() =>
            harness.RunAsync(binding, (_, _) => throw new InvalidOperationException("No function is authorized")))).Code);
        Assert.Equal(2, harness.Edge.Reads);
        Assert.Equal(0, harness.Edge.Submissions);
        Assert.True(harness.Store.ReadManagedConversation(binding.Handle)!.Retired);
        Assert.Equal(["", "reason-1"], harness.Edge.Cursors);
    }

    [Theory]
    [InlineData("[null]")]
    [InlineData("[1]")]
    [InlineData("[\"PRIVATE_HISTORY_DETAIL\"]")]
    [InlineData("[[]]")]
    [InlineData("[{}]")]
    [InlineData("[{\"id\":1,\"type\":\"reasoning\",\"summary\":[]}]")]
    [InlineData("[{\"id\":\"invalid/id\",\"type\":\"reasoning\",\"summary\":[]}]")]
    public async Task ProductionHost_ManagedHistory_MalformedItemsRetireBeforeSubmission(string json)
    {
        await using var harness = new Harness();
        await harness.InitializeAsync();
        var binding = await harness.CreateAsync();
        harness.Edge.Respond = _ => new JsonObject { ["data"] = JsonNode.Parse(json), ["has_more"] = false };
        var failure = await Assert.ThrowsAsync<ManagedProjectException>(() =>
            harness.RunAsync(binding, (_, _) => throw new InvalidOperationException("No function is authorized")));
        Assert.Equal("conversation_history_invalid", failure.Code);
        Assert.Equal(1, harness.Edge.Reads);
        Assert.Equal(0, harness.Edge.Submissions);
        Assert.True(harness.Store.ReadManagedConversation(binding.Handle)!.Retired);
    }

    [Theory]
    [InlineData(0, 1, null)]
    [InlineData(1, 0, "copilot_input_token_limit")]
    public async Task ProductionHost_ManagedInputBudget_EnforcesExactBoundaryBeforeSubmission(int extra, int submissions, string? code)
    {
        await using var harness = new Harness();
        await harness.InitializeAsync();
        var binding = await harness.CreateAsync();
        harness.Edge.Respond = request => request.Method == HttpMethod.Get
            ? new JsonObject { ["data"] = new JsonArray(), ["has_more"] = false }
            : Response(new JsonArray(Message("answer-1", Answer)));
        // Empty history plus the actual pending question and immutable wire identifiers total 121 UTF-16 units.
        var configuration = new ManagedTurnConfiguration(CopilotInputContract.SystemInstructions,
            new string('c', 64000 - 121 - CopilotInputContract.SystemInstructions.Length + extra), "");
        var operation = harness.RunAsync(binding, (_, _) => throw new InvalidOperationException("No function is authorized"), configuration);
        if (code is null) Assert.Equal(Answer, (await operation).Text);
        else Assert.Equal(code, (await Assert.ThrowsAsync<ManagedProjectException>(() => operation)).Code);
        Assert.Equal(1, harness.Edge.Reads);
        Assert.Equal(submissions, harness.Edge.Submissions);
        Assert.Equal(code is not null, harness.Store.ReadManagedConversation(binding.Handle)!.Retired);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("backoff")]
    public async Task ProductionHost_ManagedAuthorization_RechecksAfterReadAndBackoff(string boundary)
    {
        await using var harness = new Harness();
        await harness.InitializeAsync();
        var binding = await harness.CreateAsync();
        if (boundary == "backoff")
        {
            harness.Edge.ReadFailures = 1;
            harness.Time.OnBackoff = () => harness.Current = Tuple() with { EntitlementFingerprint = "revoked" };
        }
        harness.Edge.Respond = _ =>
        {
            harness.Current = Tuple() with { CaseRevision = 1 };
            return new JsonObject { ["data"] = new JsonArray(), ["has_more"] = false };
        };
        var failure = await Assert.ThrowsAsync<ManagedProjectException>(() =>
            harness.RunAsync(binding, (_, _) => throw new InvalidOperationException("No function is authorized")));
        Assert.Equal("conversation_context_mismatch", failure.Code);
        Assert.Equal(1, harness.Edge.Reads);
        Assert.Equal(0, harness.Edge.Submissions);
        Assert.Equal(boundary == "backoff" ? [TimeSpan.FromMilliseconds(500)] : [], harness.Time.Delays);
        Assert.True(harness.Store.ReadManagedConversation(binding.Handle)!.Retired);
    }

    [Theory]
    [InlineData("committed")]
    [InlineData("deadline")]
    [InlineData("retirement-sql")]
    [InlineData("cancellation")]
    [InlineData("enclosing-audit")]
    public async Task ProductionHost_ManagedNativeRelease_ReturnsExactCommittedReceiptWithoutAnotherSubmission(string boundary)
    {
        await using var harness = new Harness();
        await harness.InitializeAsync(native: true);
        var services = harness.Services;
        var cases = services.GetRequiredService<ICaseStateStore>();
        var scope = cases.AuthorizeCase("cp-case-a", cases.OwnerOf("cp-case-a")!);
        foreach (var (action, decision) in new[] { (FinanceEndpoints.ReserveDecisionAction, "approve"), (FinanceEndpoints.LegalDecisionAction, "signed-off") })
            cases.Commit(scope, cases.Read(scope).Revision, new CaseCommit(null, null, "presenter", action, JsonSerializer.Serialize(new { decision })));
        Assert.Empty(ReleaseEndpoints.Blockers(cases.Read(scope)));
        var before = cases.Read(scope);
        harness.Current = Tuple() with { CaseRevision = before.Revision };
        var session = services.GetRequiredService<DemoSessionStore>().Create(new DemoUser("owner", "Test", "presenter", ["cp-case-a"]));
        const string note = "Release to the next lessee.";
        var proposals = services.GetRequiredService<CopilotProposalService>();
        var created = proposals.Create(session, "cp-case-a", before.Revision, note);
        Assert.Equal(200, created.Status);
        var proposalId = (string)created.Body["proposalId"]!;
        Assert.Equal(200, proposals.Decide(session, "cp-case-a", proposalId, "approve").Status);
        var binding = await harness.CreateAsync();
        harness.Edge.Respond = request =>
        {
            if (request.Method == HttpMethod.Get) return new JsonObject { ["data"] = new JsonArray(), ["has_more"] = false };
            var call = Call("fc-1", "call-1", "advance_stage");
            call["arguments"] = new JsonObject
            {
                ["caseId"] = "cp-case-a", ["expectedRevision"] = before.Revision, ["proposalId"] = proposalId
            }.ToJsonString();
            return Response(new JsonArray(call, Call("fc-2", "call-2", "list_my_cases")));
        };
        var http = new DefaultHttpContext { RequestServices = services };
        http.Request.Headers.Cookie = $"__Host-demo-session={session.Id}";
        http.Items[CopilotMcp.ReleaseNoteKey] = note;
        var tools = new CopilotQuestions.ToolTurn(http, "cp-case-a", TestContext.Current.CancellationToken);
        var dispatches = 0;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        ManagedTurnResult? result = null;
        var served = await CopilotInvocation.RunAsync(services.GetRequiredService<ICopilotInvocationLedger>(), cases, session,
            "question", CopilotProvenance.CopilotClient, "cp-case-a", async token =>
        {
            result = await harness.RunAsync(binding, async (selection, toolToken) =>
            {
                dispatches++;
                try { return await tools.DispatchManagedAsync(selection, toolToken); }
                finally
                {
                    harness.Current = harness.Current with { CaseRevision = cases.Read(scope).Revision };
                    if (boundary == "deadline") harness.Time.Advance(TimeSpan.FromSeconds(30));
                    if (boundary == "retirement-sql") harness.Writes.Fail = true;
                    if (boundary == "cancellation") cancellation.Cancel();
                }
            }, cancellationToken: token);
            harness.Writes.FailOutcome = boundary == "enclosing-audit";
            return result.NativeResult!;
        }, cancellation.Token);
        Assert.Equal(1, dispatches);
        Assert.Equal(1, harness.Edge.Submissions);
        Assert.Single(harness.Edge.Inputs);
        Assert.NotNull(result);
        Assert.Equal("", result.Text);
        Assert.Null(result.ResponseId);
        Assert.Empty(result.ItemIds);
        var native = Assert.IsType<CopilotInvocationResult>(result.NativeResult);
        Assert.Equal((200, "ok", CopilotOutcome.Succeeded, false),
            (native.HttpStatus, native.ResultCode, native.Outcome, native.OutcomeRecorded));
        Assert.True(native.NativeReceiptCommitted);
        Assert.Same(native.Result, served);
        var body = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.ContentHttpResult>(native.Result);
        Assert.Equal(services.GetRequiredService<ICopilotProposalStore>().Find(Guid.Parse(proposalId))!.Receipt!.Bytes, body.ResponseContent);
        var after = cases.Read(scope);
        Assert.Equal(before.Revision + 1, after.Revision);
        Assert.Equal(before.Audit.Count + 1, after.Audit.Count);
        Assert.True(ReleaseEndpoints.IsReleased(after));
        Assert.Equal(boundary == "retirement-sql" ? 1 : 0, harness.Writes.Failures);
        Assert.Equal(boundary == "enclosing-audit" ? 1 : 0, harness.Writes.OutcomeFailures);
        if (boundary != "retirement-sql") Assert.True(harness.Store.ReadManagedConversation(binding.Handle)!.Retired);
        var records = await services.GetRequiredService<ICopilotInvocationStore>().ReadCaseAsync("cp-case-a", TestContext.Current.CancellationToken);
        var actionOutcome = Assert.Single(records, item => item.Operation == "advance_stage" && item.Phase == "outcome");
        Assert.Equal((200, "ok", "succeeded"), (actionOutcome.HttpStatus, actionOutcome.ResultCode, actionOutcome.Outcome));
        Assert.Equal(boundary == "enclosing-audit" ? 0 : 1, records.Count(item => item.Operation == "question" && item.Phase == "outcome"));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("context", "conversation_context_mismatch")]
    [InlineData("deadline", "copilot_timeout")]
    [InlineData("completion-sql", "managed_operation_ambiguous")]
    public async Task ProductionHost_ManagedProposal_TerminatesWithoutPersistingUiNoteAndStillRequiresCompletion(string? boundary, string? code)
    {
        await using var harness = new Harness();
        await harness.InitializeAsync(native: true);
        var services = harness.Services;
        var cases = services.GetRequiredService<ICaseStateStore>();
        var scope = cases.AuthorizeCase("cp-case-a", cases.OwnerOf("cp-case-a")!);
        var before = cases.Read(scope);
        harness.Current = Tuple() with { CaseRevision = before.Revision };
        var session = services.GetRequiredService<DemoSessionStore>().Create(new DemoUser("owner", "Test", "presenter", ["cp-case-a"]));
        var binding = await harness.CreateAsync();
        harness.Edge.Respond = request =>
        {
            if (request.Method == HttpMethod.Get) return new JsonObject { ["data"] = new JsonArray(), ["has_more"] = false };
            var call = Call("fc-1", "call-1", "advance_stage");
            call["arguments"] = new JsonObject { ["caseId"] = "cp-case-a", ["expectedRevision"] = before.Revision }.ToJsonString();
            return Response(new JsonArray(call, Call("fc-2", "call-2", "list_my_cases")));
        };
        var http = new DefaultHttpContext { RequestServices = services };
        http.Request.Headers.Cookie = $"__Host-demo-session={session.Id}";
        const string note = "PRIVATE_RELEASE_NOTE_SENTINEL";
        http.Items[CopilotMcp.ReleaseNoteKey] = note;
        var tools = new CopilotQuestions.ToolTurn(http, "cp-case-a", TestContext.Current.CancellationToken);
        var dispatches = 0;
        var operation = harness.RunAsync(binding, async (selection, token) =>
        {
            dispatches++;
            try { return await tools.DispatchManagedAsync(selection, token); }
            finally
            {
                if (boundary == "context") harness.Current = harness.Current with { EntitlementFingerprint = "revoked" };
                if (boundary == "deadline") harness.Time.Advance(TimeSpan.FromSeconds(30));
                if (boundary == "completion-sql") harness.Writes.Fail = true;
            }
        });
        if (code is null)
        {
            var result = await operation;
            var native = Assert.IsType<CopilotInvocationResult>(result.NativeResult);
            Assert.Equal((200, "confirmation_required", false), (native.HttpStatus, native.ResultCode, native.OutcomeRecorded));
            var body = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.ContentHttpResult>(native.Result);
            var json = JsonNode.Parse(body.ResponseContent!)!;
            Assert.Equal(CopilotQuestions.ConfirmationAnswer, (string?)json["answer"]);
            Assert.Equal(note, (string?)json["confirmation"]!["note"]);
        }
        else
        {
            Assert.Equal(code, (await Assert.ThrowsAsync<ManagedProjectException>(() => operation)).Code);
            Assert.True(harness.Store.ReadManagedConversation(binding.Handle)!.Retired);
        }
        Assert.Equal(1, dispatches);
        Assert.Equal(1, harness.Edge.Submissions);
        Assert.Equal(ManagedHistoryContract.CopilotQuestion("What should I review?").ToJsonString(),
            Assert.Single(harness.Edge.Inputs)!.ToJsonString());
        var after = cases.Read(scope);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.Audit.Count, after.Audit.Count);
        Assert.False(ReleaseEndpoints.IsReleased(after));
        Assert.Equal(boundary == "completion-sql" ? 1 : 0, harness.Writes.Failures);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("case")]
    [InlineData("role")]
    [InlineData("evidence-version")]
    [InlineData("evidence-hashes")]
    public async Task ProductionHost_ManagedInvestigator_UsesRealReadToolsRegistryAndClaimVerification(string? mismatch)
    {
        const string evidence = "SYNTHETIC EVIDENCE: aircraft evidence v1.";
        await using var harness = new Harness();
        var source = new ManagedHistoryContractTests.ManagedClaimSource(evidence, "case-001");
        var turn = new ManagedInvestigatorTurn("case-001", "technical-reviewer", source,
            new(new TechnicalSource(), new MaintenanceSource(), new FinanceSource(), new RecordsSource(), new ContractsSource()),
            FakeApprovedManifest.Approved(), new("evidence-v1", "swedencentral", evidence), SyntheticContentRegistry.FromCommittedFixtures());
        harness.Current = Tuple() with
        {
            CaseId = "case-001", Role = "technical-reviewer", AgentName = "airline-investigator",
            EvidenceVersion = "evidence-v1", EvidenceHashesDigest = turn.EvidenceHashesDigest
        };
        harness.Current = mismatch switch
        {
            "case" => harness.Current with { CaseId = "case-002" },
            "role" => harness.Current with { Role = "finance-reviewer" },
            "evidence-version" => harness.Current with { EvidenceVersion = "evidence-v2" },
            "evidence-hashes" => harness.Current with { EvidenceHashesDigest = new string('b', 64) },
            _ => harness.Current
        };
        await harness.InitializeAsync();
        var binding = await harness.CreateAsync("investigator");
        var answer = new JsonArray(new JsonObject
        {
            ["text"] = evidence,
            ["witnesses"] = new JsonArray(new JsonObject { ["documentId"] = "document-1", ["page"] = 1, ["quote"] = evidence }),
            ["sourceWitnesses"] = new JsonArray(new JsonObject
            {
                ["sourceId"] = "technical", ["recordId"] = "tech-airworthiness-001", ["recordVersion"] = 1
            })
        }).ToJsonString();
        harness.Edge.Respond = request =>
        {
            if (request.Method == HttpMethod.Get) return new JsonObject { ["data"] = new JsonArray(), ["has_more"] = false };
            JsonObject response;
            if (harness.Edge.Submissions == 1)
            {
                var page = Call("fc-1", "call-1", CaseReadTools.SourcePageTool);
                page["arguments"] = "{\"documentId\":\"document-1\",\"page\":1}";
                var record = Call("fc-2", "call-2", SourceReadTools.GetTool);
                record["arguments"] = "{\"sourceId\":\"technical\",\"recordId\":\"tech-airworthiness-001\"}";
                response = Response(new JsonArray(page, record));
            }
            else response = Response(new JsonArray(Message("answer-1", answer)));
            response["agent_reference"]!["name"] = "airline-investigator";
            return response;
        };
        var operation = harness.Services.GetRequiredService<ManagedTurnCoordinator>().RunAsync("investigator", binding, () => harness.Current,
            new("investigation", "run-1", "managed", "case-001", Digest, "v1", "call-1"),
            new(BoundedInvestigator.Instructions, "configuration", JsonSerializer.Serialize(turn.Functions.Select(function =>
                new { function.Name, function.Description, Parameters = function.JsonSchema }))),
            turn.Policy, turn.Input, turn.DispatchAsync, TestContext.Current.CancellationToken);
        if (mismatch is not null)
        {
            Assert.Equal("conversation_context_mismatch", (await Assert.ThrowsAsync<ManagedProjectException>(() => operation)).Code);
            Assert.Equal(0, harness.Edge.Reads);
            Assert.Equal(0, harness.Edge.Submissions);
            Assert.Equal(0, source.InventoryReads);
            Assert.Equal(0, source.PageReads);
            Assert.True(harness.Store.ReadManagedConversation(binding.Handle)!.Retired);
            return;
        }
        var result = await operation;
        Assert.Equal(answer, result.Text);
        Assert.Null(result.NativeResult);
        Assert.Equal(2, harness.Edge.Submissions);
        Assert.Equal(2, harness.Edge.Inputs[1]!.AsArray().Count);
        Assert.Equal(BoundedInvestigator.BuildPrompt([evidence]), (string?)harness.Edge.Inputs[0]![0]!["content"]![0]!["text"]);
        var claim = Assert.Single(turn.Claims!);
        Assert.Equal(evidence, claim.Text);
        Assert.Equal(new SourceRecordWitness("technical", "tech-airworthiness-001", 1), Assert.Single(claim.VerifiedSourceBasis!));
        Assert.Equal(2, source.PageReads);
        Assert.Equal(2, source.InventoryReads);
        var completed = harness.Store.ReadManagedConversation(binding.Handle)!;
        Assert.False(completed.Retired);
        Assert.Null(completed.ActiveFence);
        Assert.Equal(1, completed.AdmittedTurnCount);
    }

    private static JsonObject Call(string id, string callId, string name) => new()
    {
        ["id"] = id, ["type"] = "function_call", ["call_id"] = callId, ["name"] = name, ["arguments"] = "{}"
    };

    private static JsonObject Message(string id, string text) => new()
    {
        ["id"] = id, ["type"] = "output_message", ["status"] = "completed", ["role"] = "assistant",
        ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = text })
    };

    private static JsonObject Response(JsonArray output) => new()
    {
        ["id"] = "resp-2", ["object"] = "response", ["status"] = "completed",
        ["conversation"] = new JsonObject { ["id"] = "conv-1" }, ["output"] = output,
        ["agent_reference"] = new JsonObject { ["type"] = "agent_reference", ["name"] = "airline-copilot", ["version"] = "1" },
        ["max_output_tokens"] = 2000, ["truncation"] = "disabled", ["background"] = false
    };

    private sealed class Harness : IAsyncDisposable
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"managed-runtime-{Guid.NewGuid():N}.json");
        private WebApplicationFactory<Program> _host = null!;
        public readonly Edge Edge = new();
        public readonly RetryTime Time = new();
        public readonly ManagedWriteFailure Writes = new();
        public ManagedConversationTuple Current = Tuple();
        public IServiceProvider Services => _host.Services;
        public IManagedConversationStore Store => _host.Services.GetRequiredService<IManagedConversationStore>();

        public async Task InitializeAsync(bool native = false)
        {
            await _connection.OpenAsync(TestContext.Current.CancellationToken);
            var options = new DbContextOptionsBuilder<CaseStateDbContext>().UseSqlite(_connection).AddInterceptors(Writes).Options;
            using (var context = new CaseStateDbContext(options)) await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
            if (!native) new SqlCaseStateStore(options).CreateCase(Current.CaseId, Current.UserId);
            _host = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Demo:Passcode"] = "test-passcode", ["CaseState:Path"] = _path,
                    ["ModelGateway:Endpoint"] = "https://gateway.private.invalid", ["ModelGateway:Audience"] = "api://managed-gateway",
                    ["Foundry:GatewayEndpoint"] = "https://gateway.private.invalid/foundry/projects/airline-demo",
                    ["Foundry:ProjectName"] = "airline-demo", ["Foundry:Agents:copilot:Name"] = "airline-copilot",
                    ["Foundry:Agents:copilot:Version"] = "1", ["Foundry:Agents:copilot:ConfigurationId"] = Digest,
                    ["Foundry:Agents:investigator:Name"] = "airline-investigator", ["Foundry:Agents:investigator:Version"] = "1",
                    ["Foundry:Agents:investigator:ConfigurationId"] = Digest,
                    ["Demo:SeedCases"] = native ? "true" : "false", ["Demo:SeedJourney"] = native ? "true" : "false"
                }));
                builder.ConfigureTestServices(services =>
                {
                    services.AddSingleton(options);
                    services.AddSingleton<TimeProvider>(Time);
                    services.AddSingleton<HttpMessageHandler>(Edge);
                    services.AddSingleton<Func<TokenCredential>>(_ => () => new Credential());
                    if (native)
                    {
                        services.AddSingleton<ICaseStateStore>(new SqlCaseStateStore(options));
                        services.AddSingleton<ICopilotProposalStore>(new SqlCopilotProposalStore(options));
                        services.AddSingleton<ICopilotInvocationStore>(new SqlCopilotInvocationStore(options));
                        services.AddSingleton<ICopilotInvocationDetailStore>(new SqlCopilotInvocationDetailStore(options));
                    }
                });
            });
        }

        public Task<ManagedConversationSnapshot> CreateAsync(string workload = "copilot") => _host.Services.GetRequiredService<ManagedConversationLifecycle>()
            .CreateAsync(workload, Current, Correlation(), () => Current, TestContext.Current.CancellationToken);

        public Task<ManagedTurnResult> RunAsync(ManagedConversationSnapshot binding,
            Func<ManagedToolSelection, CancellationToken, Task<JsonObject>> dispatch, ManagedTurnConfiguration? configuration = null,
            CancellationToken? cancellationToken = null) =>
            _host.Services.GetRequiredService<ManagedTurnCoordinator>().RunAsync("copilot", binding, () => Current, Correlation(),
                configuration ?? new(CopilotInputContract.SystemInstructions, "configuration", "tools"), ManagedHistoryContract.CopilotPolicy("cp-case-a"),
                ManagedHistoryContract.CopilotQuestion("What should I review?"), dispatch, cancellationToken ?? TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync()
        {
            if (_host is not null) await _host.DisposeAsync();
            await _connection.DisposeAsync();
            File.Delete(_path);
        }
    }

    private sealed class Credential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("synthetic-token", DateTimeOffset.UtcNow.AddMinutes(5));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class ManagedWriteFailure : DbCommandInterceptor
    {
        public bool Fail;
        public int Failures;
        public bool FailOutcome;
        public int OutcomeFailures;
        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            if (Fail && command.CommandText.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("managed_conversations", StringComparison.Ordinal))
            {
                Fail = false;
                Failures++;
                throw new InvalidOperationException("PRIVATE_SQL_RETIREMENT_DETAIL");
            }
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (FailOutcome && command.CommandText.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("copilot_invocations", StringComparison.Ordinal))
            {
                FailOutcome = false;
                OutcomeFailures++;
                throw new InvalidOperationException("PRIVATE_SQL_AUDIT_DETAIL");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class Edge : HttpMessageHandler
    {
        public int Submissions;
        public int Reads;
        public int ReadFailures;
        public HttpStatusCode ReadStatus = HttpStatusCode.OK;
        public readonly List<string> Cursors = [];
        public readonly List<JsonNode?> Inputs = [];
        public Func<HttpRequestMessage, JsonObject>? Respond;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/conversations", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"conv-1\"}") };
            if (request.Method == HttpMethod.Post)
            {
                Submissions++;
                Inputs.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!["input"]!.DeepClone());
            }
            else if (request.Method == HttpMethod.Get)
            {
                Reads++;
                var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri!.Query);
                Cursors.Add(query.TryGetValue("after", out var after) ? after.ToString() : "");
                var status = Reads <= ReadFailures ? HttpStatusCode.ServiceUnavailable : ReadStatus;
                if (status != HttpStatusCode.OK) return new(status) { Content = new StringContent("PRIVATE_PROVIDER_DETAIL") };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(Respond!(request).ToJsonString()) };
        }
    }

    private sealed class RetryTime : TimeProvider
    {
        public readonly List<TimeSpan> Delays = [];
        public Action? OnBackoff;
        private TimeSpan _elapsed;
        public void Advance(TimeSpan by) => _elapsed += by;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + _elapsed;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime > TimeSpan.Zero && dueTime <= TimeSpan.FromSeconds(1))
            {
                Delays.Add(dueTime);
                _elapsed += dueTime;
                OnBackoff?.Invoke();
                return TimeProvider.System.CreateTimer(callback, state, TimeSpan.FromMilliseconds(1), period);
            }
            return TimeProvider.System.CreateTimer(callback, state, dueTime, period);
        }
    }
}

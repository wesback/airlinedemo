using System.Text.Json.Nodes;
using AirlineDemo.Api.Foundry;
using AirlineDemo.Api.Investigation;
using AirlineDemo.Api.SourceSystems;

namespace AirlineDemo.Api.Tests;

public sealed class ManagedHistoryContractTests
{
    private static ManagedHistoryPolicy Policy() => new(
        text => text == "Approved question",
        text => text == "{\"answer\":\"Approved answer\",\"citations\":[]}",
        (name, arguments) => name == "get_case_summary" && arguments.ToJsonString() == "{\"caseId\":\"CASE-01\"}",
        (name, arguments, result) => name == "get_case_summary" && arguments["caseId"]!.GetValue<string>() == "CASE-01" &&
            result.ToJsonString() == "{\"contractVersion\":1,\"httpStatus\":200}");

    [Theory]
    [InlineData("message", "user", "input_text", "Approved question")]
    [InlineData("message", "assistant", "output_text", "{\"answer\":\"Approved answer\",\"citations\":[]}")]
    [InlineData("output_message", "assistant", "output_text", "{\"answer\":\"Approved answer\",\"citations\":[]}")]
    public void ManagedHistory_SeparatelyDocumentedMessagesAreValidated(string type, string role, string contentType, string text)
    {
        var items = new JsonArray(new JsonObject
        {
            ["id"] = "msg_1", ["type"] = type, ["role"] = role, ["status"] = "completed",
            ["content"] = new JsonArray(new JsonObject { ["type"] = contentType, ["text"] = text })
        });
        var history = ManagedHistoryContract.Validate(items.ToJsonString(), Policy());
        Assert.Equal(items.ToJsonString(), history.Json);
        Assert.Equal(64, history.Digest.Length);
        var copy = history.CopyItems();
        copy[0]!["role"] = "system";
        Assert.Equal(items.ToJsonString(), history.Json);
        Assert.Equal(history.Json, ManagedHistoryContract.Validate(history.Json, Policy(), history.Digest).Json);
        Assert.Equal("conversation_history_invalid", Assert.Throws<ManagedProjectException>(() =>
            ManagedHistoryContract.Validate(history.Json, Policy(), new string('b', 64))).Code);
    }

    [Theory]
    [InlineData("[{\"id\":\"a\",\"type\":\"configuration_update\"}]")]
    [InlineData("[{\"id\":\"a\",\"type\":\"opaque_item\"}]")]
    [InlineData("[{\"id\":\"a\",\"type\":\"reasoning\",\"summary\":[],\"encrypted_content\":\"opaque\"}]")]
    [InlineData("[{\"id\":\"a\",\"type\":\"reasoning\",\"summary\":[],\"surprise\":\"raw source\"}]")]
    [InlineData("[{\"id\":\"a\",\"type\":\"reasoning\",\"summary\":[{\"type\":\"summary_text\",\"text\":\"ok\",\"secret\":\"value\"}]}]")]
    [InlineData("[{\"id\":\"a\",\"type\":\"reasoning\",\"summary\":[]},{\"id\":\"a\",\"type\":\"reasoning\",\"summary\":[]}]")]
    [InlineData("[{\"id\":\"a\",\"id\":\"b\",\"type\":\"reasoning\",\"summary\":[]}]")]
    [InlineData("[{\"id\":\"a\",\"type\":\"message\",\"status\":\"completed\",\"role\":\"system\",\"content\":[]}]")]
    [InlineData("[{\"id\":\"a\",\"type\":\"message\",\"status\":\"in_progress\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"Approved question\"}]}]")]
    [InlineData("[{\"id\":\"a\",\"type\":\"message\",\"status\":\"completed\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"edited question\"}]}]")]
    [InlineData("[{\"id\":\"a\",\"type\":\"output_message\",\"status\":\"completed\",\"role\":\"assistant\",\"content\":[{\"type\":\"refusal\",\"refusal\":\"no\"}]}]")]
    [InlineData("[{\"id\":\"a\",\"type\":\"output_message\",\"status\":\"completed\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"arbitrary answer\",\"annotations\":[{\"url\":\"foreign\"}]}]}]")]
    [InlineData("[{\"id\":\"a\",\"type\":\"function_call_output\",\"call_id\":\"unknown\",\"output\":\"{}\"}]")]
    public void ManagedHistory_UnknownEditedOpaqueAndUncorrelatedShapesFailClosed(string json)
    {
        Assert.Equal("conversation_history_invalid",
            Assert.Throws<ManagedProjectException>(() => ManagedHistoryContract.Validate(json, Policy())).Code);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("namespace", true)]
    [InlineData("async", true)]
    [InlineData("arguments", true)]
    [InlineData("output", true)]
    [InlineData("call_id", true)]
    [InlineData("duplicate", true)]
    public void ManagedHistory_FunctionResultsRequireExactArgumentsAndOutputCorrelation(string? mutation, bool rejected)
    {
        var items = new JsonArray(
            new JsonObject { ["id"] = "fc_1", ["type"] = "function_call", ["call_id"] = "call_1", ["name"] = "get_case_summary", ["arguments"] = "{\"caseId\":\"CASE-01\"}" },
            new JsonObject { ["id"] = "out_1", ["type"] = "function_call_output", ["call_id"] = "call_1", ["output"] = "{\"contractVersion\":1,\"httpStatus\":200}" });
        switch (mutation)
        {
            case "namespace": items[0]!["namespace"] = "foreign"; break;
            case "async": items[0]!["async"] = true; break;
            case "arguments": items[0]!["arguments"] = "{\"caseId\":\"CASE-01\",\"caseId\":\"FOREIGN\"}"; break;
            case "output": items[1]!["output"] = "{\"contractVersion\":1,\"httpStatus\":200,\"rawSource\":\"SENTINEL\"}"; break;
            case "call_id": items[1]!["call_id"] = "other"; break;
            case "duplicate": var duplicate = items[1]!.DeepClone(); duplicate["id"] = "out_2"; items.Add(duplicate); break;
        }
        if (rejected) Assert.Equal("conversation_history_invalid",
            Assert.Throws<ManagedProjectException>(() => ManagedHistoryContract.Validate(items.ToJsonString(), Policy())).Code);
        else Assert.Equal(items.ToJsonString(), ManagedHistoryContract.Validate(items.ToJsonString(), Policy()).Json);
    }

    [Fact]
    public void ManagedInputBudget_CountsHistoryConfigurationSchemaArgumentsAndIdentifiers()
    {
        var history = ManagedHistoryContract.Validate(
            "[{\"id\":\"a\",\"type\":\"reasoning\",\"summary\":[{\"type\":\"summary_text\",\"text\":\"observable\"}]}]", Policy());
        Assert.Equal(32, history.ObservableUtf16Units);
        var instructions = new string('a', 63968);
        Assert.Equal(16000, ManagedHistoryContract.EstimateInputTokens(history, instructions, "", "", new JsonArray()));
        foreach (var location in new[] { "instructions", "configuration", "tools", "pending", "identifiers" })
        {
            var failure = Assert.Throws<ManagedProjectException>(() => ManagedHistoryContract.EstimateInputTokens(history,
                instructions + (location == "instructions" ? "a" : ""), location == "configuration" ? "a" : "",
                location == "tools" ? "a" : "", location == "pending" ? new JsonArray("a") : new JsonArray(),
                location == "identifiers" ? ["a"] : []));
            Assert.Equal("copilot_input_token_limit", failure.Code);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("unreturned-citation")]
    [InlineData("extra-error-field")]
    [InlineData("missing-output")]
    public void CopilotSummaries_AcceptsClosedToolDenialsWithoutTrustingTheirContent(string? mutation)
    {
        var error = JsonNode.Parse("""{"contractVersion":1,"httpStatus":403,"error":{"code":"selected_case_mismatch"}}""")!;
        if (mutation == "extra-error-field") error["error"]!["text"] = "FOREIGN_CONTENT";
        var answer = JsonNode.Parse("""{"answer":"The other case was not accessed.","citations":[]}""")!;
        if (mutation == "unreturned-citation")
            answer["citations"]!.AsArray().Add(JsonNode.Parse("""{"sourceId":"technical","recordId":"unread","recordVersion":1}"""));
        var items = ManagedHistoryContract.CopilotQuestion("Summarize the selected case.");
        items[0]!["id"] = "user-1";
        items[0]!["type"] = "message";
        items[0]!["status"] = "completed";
        items.Add(new JsonObject
        {
            ["id"] = "call-1", ["type"] = "function_call", ["call_id"] = "denied-1",
            ["name"] = "get_case_summary", ["arguments"] = """{"caseId":"cp-case-b"}"""
        });
        if (mutation != "missing-output")
            items.Add(new JsonObject
            {
                ["id"] = "output-1", ["type"] = "function_call_output", ["call_id"] = "denied-1",
                ["output"] = error.ToJsonString()
            });
        items.Add(new JsonObject
        {
            ["id"] = "answer-1", ["type"] = "message", ["status"] = "completed", ["role"] = "assistant",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = answer.ToJsonString() })
        });
        if (mutation is not null)
            Assert.Equal("conversation_history_invalid", Assert.Throws<ManagedProjectException>(() =>
                ManagedHistoryContract.CopilotSummaries(items.ToJsonString(), "cp-case-a")).Code);
        else
        {
            var summary = Assert.Single(ManagedHistoryContract.CopilotSummaries(items.ToJsonString(), "cp-case-a"));
            Assert.Equal("Summarize the selected case.", summary.Question);
            Assert.Equal("The other case was not accessed.", summary.Answer);
            Assert.Empty(summary.Citations);
        }
    }

    [Fact]
    public void ManagedCopilotObservation_ReusesStructuralProjectionWithoutRawSourceOrNotes()
    {
        var structured = new JsonObject
        {
            ["contractVersion"] = 1, ["httpStatus"] = 200,
            ["result"] = new JsonObject { ["text"] = "RAW_SOURCE_SENTINEL" },
            ["uiOnly"] = new JsonObject { ["note"] = "NOTE_SENTINEL", ["cookie"] = "TOKEN_SENTINEL" },
            ["modelSafe"] = new JsonObject
            {
                ["caseId"] = "CASE-01", ["sourceId"] = "technical", ["outcome"] = "ok",
                ["refs"] = new JsonArray(new JsonObject { ["sourceId"] = "technical", ["recordId"] = "record-1", ["recordVersion"] = 1 })
            }
        };
        var result = ManagedHistoryContract.CopilotObservation("read_source", "record", structured);
        Assert.Equal("{\"contractVersion\":1,\"httpStatus\":200,\"result\":{\"caseId\":\"CASE-01\",\"sourceId\":\"technical\",\"outcome\":\"ok\",\"refs\":[{\"sourceId\":\"technical\",\"recordId\":\"record-1\",\"recordVersion\":1}]}}",
            result.ToJsonString());
        structured["modelSafe"]!["extra"] = "SENTINEL";
        Assert.Equal("copilot_context_invalid", Assert.Throws<ManagedProjectException>(() =>
            ManagedHistoryContract.CopilotObservation("read_source", "record", structured)).Code);
    }

    [Theory]
    [InlineData(2000, true)]
    [InlineData(2001, false)]
    [InlineData(0, false)]
    public void ManagedCopilotQuestion_PreservesExistingScalarBound(int scalars, bool accepted)
    {
        var question = string.Concat(Enumerable.Repeat("\U0001F680", scalars));
        if (accepted)
            Assert.Equal(question, ManagedHistoryContract.CopilotQuestion(question)[0]!["content"]![0]!["text"]!.GetValue<string>());
        else Assert.Equal("invalid_question", Assert.Throws<ManagedProjectException>(() => ManagedHistoryContract.CopilotQuestion(question)).Code);
    }

    [Fact]
    public void ManagedInvestigatorInput_UsesExactRegistryAndOriginalDelimiterGate()
    {
        const string evidence = "SYNTHETIC EVIDENCE: aircraft evidence v1.";
        var registry = SyntheticContentRegistry.FromCommittedFixtures();
        var manifest = FakeApprovedManifest.Approved();
        var request = new ModelRequest("evidence-v1", "swedencentral", evidence);
        var accepted = ManagedHistoryContract.InvestigatorDocuments(manifest, request, registry);
        Assert.Equal(BoundedInvestigator.BuildPrompt([evidence]), accepted[0]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Throws<ModelGateException>(() => ManagedHistoryContract.InvestigatorDocuments(manifest,
            request with { Evidence = ["SYNTHETIC EVIDENCE: unregistered prose."] }, registry));
        Assert.Throws<ArgumentException>(() => ManagedHistoryContract.InvestigatorDocuments(manifest,
            request with { Evidence = [evidence + BoundedInvestigator.UntrustedBegin] }, registry));
        Assert.Throws<ModelGateException>(() => ManagedHistoryContract.InvestigatorDocuments(manifest,
            request with { DataRegion = "other" }, registry));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagedInvestigatorInventory_UsesOnlyExactCaseMetadata(bool tamperInventory)
    {
        const string evidence = "SYNTHETIC EVIDENCE: aircraft evidence v1.";
        var source = new ManagedClaimSource(evidence, "case-001");
        var turn = new ManagedInvestigatorTurn("case-001", "presenter", source,
            new(new TechnicalSource(), new MaintenanceSource(), new FinanceSource(), new RecordsSource(), new ContractsSource()),
            FakeApprovedManifest.Approved(), new ModelRequest("evidence-v1", "swedencentral", evidence),
            SyntheticContentRegistry.FromCommittedFixtures());
        var args = new JsonObject();
        var output = await turn.DispatchAsync(new("inventory-1", CaseReadTools.InventoryTool, args),
            TestContext.Current.CancellationToken);
        Assert.Equal($"{BoundedInvestigator.UntrustedBegin}\ndocument-1\n{BoundedInvestigator.UntrustedEnd}", (string?)output["text"]);
        if (tamperInventory) output["text"] = $"{BoundedInvestigator.UntrustedBegin}\nforeign-document\n{BoundedInvestigator.UntrustedEnd}";
        Assert.Equal(!tamperInventory, turn.Policy.ToolOutput(CaseReadTools.InventoryTool, args, output));
        Assert.Equal(0, source.PageReads);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("\n", null)]
    [InlineData("\r\n", null)]
    [InlineData("\r\n\r\n", null)]
    [InlineData(" ", "model request refused: evidence SHA-256 is not registered as synthetic content")]
    [InlineData("\nUNREGISTERED", "model request refused: content has characters outside the synthetic evidence allowlist")]
    public async Task ManagedInvestigatorPage_NormalizesOnlyTrailingLineEndings(string suffix, string? error)
    {
        const string evidence = "SYNTHETIC EVIDENCE: aircraft evidence v1.";
        var source = new ManagedClaimSource(evidence + suffix, "case-001");
        var turn = new ManagedInvestigatorTurn("case-001", "presenter", source,
            new(new TechnicalSource(), new MaintenanceSource(), new FinanceSource(), new RecordsSource(), new ContractsSource()),
            FakeApprovedManifest.Approved(), new ModelRequest("evidence-v1", "swedencentral", evidence),
            SyntheticContentRegistry.FromCommittedFixtures());
        var args = new JsonObject { ["documentId"] = "document-1", ["page"] = 1 };
        async Task<JsonObject> Read() => await turn.DispatchAsync(new("page-1", CaseReadTools.SourcePageTool, args),
            TestContext.Current.CancellationToken);
        if (error is not null)
            Assert.Equal(error, (await Assert.ThrowsAsync<ModelGateException>(Read)).Message);
        else
        {
            var output = await Read();
            Assert.Equal($"{BoundedInvestigator.UntrustedBegin}\n{evidence}{suffix}\n{BoundedInvestigator.UntrustedEnd}", (string?)output["text"]);
            Assert.True(turn.Policy.ToolOutput(CaseReadTools.SourcePageTool, args, output));
        }
        Assert.Equal(1, source.PageReads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ManagedInvestigatorHistory_RestoresOnlyExactCommittedSourceWitnesses(bool tamperRecord)
    {
        const string evidence = "SYNTHETIC EVIDENCE: aircraft evidence v1.";
        var registry = SyntheticContentRegistry.FromCommittedFixtures();
        var manifest = FakeApprovedManifest.Approved();
        var request = new ModelRequest("evidence-v1", "swedencentral", evidence);
        var source = new ManagedClaimSource(evidence, "case-001");
        var turn = new ManagedInvestigatorTurn("case-001", "technical-reviewer", source,
            new(new TechnicalSource(), new MaintenanceSource(), new FinanceSource(), new RecordsSource(), new ContractsSource()),
            manifest, request, registry);
        var committed = new TechnicalSource().CommittedRecords.Single(record =>
            record.CaseId == "case-001" && record.RecordId == "tech-airworthiness-001");
        var recordJson = SourceReadTools.SerializeRecord(committed);
        if (tamperRecord) recordJson = recordJson.Replace("tech-airworthiness-001", "foreign-record", StringComparison.Ordinal);
        var output = $"{BoundedInvestigator.UntrustedBegin}\n[{recordJson}]\n{BoundedInvestigator.UntrustedEnd}";
        var claim = new JsonObject
        {
            ["text"] = evidence,
            ["witnesses"] = new JsonArray(new JsonObject { ["documentId"] = "document-1", ["page"] = 1, ["quote"] = evidence }),
            ["sourceWitnesses"] = new JsonArray(new JsonObject
            {
                ["sourceId"] = "technical", ["recordId"] = "tech-airworthiness-001", ["recordVersion"] = 1
            })
        };
        var history = new JsonArray(
            new JsonObject
            {
                ["id"] = "user-1", ["type"] = "message", ["status"] = "completed", ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = BoundedInvestigator.BuildPrompt([evidence]) })
            },
            new JsonObject
            {
                ["id"] = "call-1", ["type"] = "function_call", ["call_id"] = "read-1",
                ["name"] = SourceReadTools.ListTool, ["arguments"] = "{\"sourceId\":\"technical\"}"
            },
            new JsonObject
            {
                ["id"] = "output-1", ["type"] = "function_call_output", ["call_id"] = "read-1",
                ["output"] = new JsonObject { ["text"] = output }.ToJsonString()
            },
            new JsonObject
            {
                ["id"] = "answer-1", ["type"] = "output_message", ["status"] = "completed", ["role"] = "assistant",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = new JsonArray(claim).ToJsonString() })
            });

        if (tamperRecord)
        {
            Assert.Equal("conversation_history_invalid",
                Assert.Throws<ManagedProjectException>(() => ManagedHistoryContract.Validate(history.ToJsonString(), turn.Policy)).Code);
            Assert.Null(turn.Claims);
            Assert.Equal(0, source.PageReads);
        }
        else
        {
            _ = ManagedHistoryContract.Validate(history.ToJsonString(), turn.Policy);
            Assert.Equal(evidence, Assert.Single(turn.Claims!).Text);
            Assert.Equal(1, source.PageReads);
        }
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("unknown", "model output is not the claim contract")]
    [InlineData("duplicate", "model output is not the claim contract")]
    [InlineData("case", "model output is not the claim contract")]
    [InlineData("witness-extra", "model output is not the claim contract")]
    [InlineData("page", "model output is not the claim contract")]
    [InlineData("source-extra", "model output is not the claim contract")]
    [InlineData("foreign-document", "witness is not a document of this case")]
    [InlineData("fabricated-quote", "witness quote is not on the cited page")]
    [InlineData("paraphrase", "claim text is not supported by its witness quote")]
    [InlineData("foreign-source", "source record witness is not in the current basis")]
    public void ManagedInvestigatorAnswer_UsesClosedShapeAndActualClaimVerifier(string? mutation, string? error)
    {
        const string evidence = "SYNTHETIC EVIDENCE: aircraft evidence v1.";
        var source = new ManagedClaimSource(evidence);
        var claim = new JsonObject
        {
            ["text"] = evidence,
            ["witnesses"] = new JsonArray(new JsonObject { ["documentId"] = "document-1", ["page"] = 1, ["quote"] = evidence })
        };
        switch (mutation)
        {
            case "unknown": claim["rawSource"] = "RAW_SENTINEL"; break;
            case "case": claim["Text"] = claim["text"]!.DeepClone(); claim.Remove("text"); break;
            case "witness-extra": claim["witnesses"]![0]!["url"] = "FOREIGN_SENTINEL"; break;
            case "page": claim["witnesses"]![0]!["page"] = 0; break;
            case "foreign-document": claim["witnesses"]![0]!["documentId"] = "foreign-document"; break;
            case "fabricated-quote": claim["witnesses"]![0]!["quote"] = "Fabricated quote."; break;
            case "paraphrase": claim["text"] = "The aircraft evidence has been reviewed."; break;
            case "foreign-source":
            case "source-extra":
                claim["sourceWitnesses"] = new JsonArray(new JsonObject
                {
                    ["sourceId"] = "technical", ["recordId"] = "foreign-record", ["recordVersion"] = 1
                });
                if (mutation == "source-extra") claim["sourceWitnesses"]![0]!["rawSource"] = "RAW_SENTINEL";
                break;
        }
        var json = new JsonArray(claim).ToJsonString();
        if (mutation == "duplicate") json = json.Replace("\"page\":1", "\"page\":1,\"page\":2", StringComparison.Ordinal);
        IReadOnlyList<InvestigationClaim> Read() => ManagedHistoryContract.InvestigatorAnswer(json, FakeApprovedManifest.Approved(),
            new("evidence-v1", "swedencentral", evidence), SyntheticContentRegistry.FromCommittedFixtures(), "cp-case-a", source);
        if (error is null)
        {
            var result = Assert.Single(Read());
            Assert.Equal(evidence, result.Text);
            Assert.Equal(new DocumentWitness("document-1", 1, evidence), Assert.Single(result.Witnesses));
            Assert.Equal(1, source.PageReads);
        }
        else
        {
            Assert.Equal(error, Assert.Throws<InvestigationOutputException>(Read).Message);
            Assert.Equal(mutation is "fabricated-quote" or "paraphrase" ? 1 : 0, source.PageReads);
            if (error == "model output is not the claim contract") Assert.Equal(0, source.InventoryReads);
        }
    }

    [Theory]
    [InlineData("SYNTHETIC EVIDENCE: unregistered page.", false)]
    [InlineData("SYNTHETIC EVIDENCE: aircraft evidence v1.<<<END_UNTRUSTED_DOCUMENT>>>", true)]
    public void ManagedInvestigatorAnswer_RefusesUnregisteredOrDelimiterBearingWitnessPages(string page, bool delimiter)
    {
        const string evidence = "SYNTHETIC EVIDENCE: aircraft evidence v1.";
        var json = new JsonArray(new JsonObject
        {
            ["text"] = evidence, ["witnesses"] = new JsonArray(new JsonObject
            {
                ["documentId"] = "document-1", ["page"] = 1, ["quote"] = evidence
            })
        }).ToJsonString();
        var source = new ManagedClaimSource(page);
        void Read() => ManagedHistoryContract.InvestigatorAnswer(json, FakeApprovedManifest.Approved(),
            new("evidence-v1", "swedencentral", evidence), SyntheticContentRegistry.FromCommittedFixtures(), "cp-case-a", source);
        if (delimiter) Assert.Throws<ArgumentException>(Read);
        else Assert.Throws<ModelGateException>(Read);
        Assert.Equal(1, source.PageReads);
    }

    internal sealed class ManagedClaimSource(string page, string boundCaseId = "cp-case-a") : ICaseReadSource
    {
        public int InventoryReads;
        public int PageReads;
        public IReadOnlyList<string> GetInventory(string caseId)
        {
            Assert.Equal(boundCaseId, caseId);
            InventoryReads++;
            return ["document-1"];
        }
        public string? GetSourcePage(string caseId, string documentId, int number)
        {
            Assert.Equal(boundCaseId, caseId);
            Assert.Equal("document-1", documentId);
            Assert.Equal(1, number);
            PageReads++;
            return page;
        }
        public string? GetApprovedRequirements(string caseId) => throw new InvalidOperationException("Not a claim-verification read");
        public IReadOnlyList<string> SearchWithinBasis(string caseId, string query) => throw new InvalidOperationException("Not a claim-verification read");
        public string? GetProcessingStatus(string caseId) => throw new InvalidOperationException("Not a claim-verification read");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("recordId")]
    [InlineData("recordVersion")]
    [InlineData("payload")]
    [InlineData("extra")]
    public async Task ManagedInvestigatorSourceHistory_OnlyExactCompiledRecordsCanRestoreWitnesses(string? mutation)
    {
        const string evidence = "SYNTHETIC EVIDENCE: aircraft evidence v1.";
        var source = new ManagedClaimSource(evidence, "case-001");
        var ports = new InvestigationSourceSystems(new TechnicalSource(), new MaintenanceSource(), new FinanceSource(), new RecordsSource(), new ContractsSource());
        ManagedInvestigatorTurn Create() => new("case-001", "technical-reviewer", source, ports, FakeApprovedManifest.Approved(),
            new("evidence-v1", "swedencentral", evidence), SyntheticContentRegistry.FromCommittedFixtures());
        var turn = Create();
        var arguments = new JsonObject { ["sourceId"] = "technical", ["recordId"] = "tech-airworthiness-001" };
        var output = await turn.DispatchAsync(new("call-1", SourceReadTools.GetTool, arguments), TestContext.Current.CancellationToken);
        if (mutation is not null)
        {
            var raw = output["text"]!.GetValue<string>();
            var begin = BoundedInvestigator.UntrustedBegin + "\n";
            var end = "\n" + BoundedInvestigator.UntrustedEnd;
            var record = JsonNode.Parse(raw[begin.Length..^end.Length])!;
            if (mutation == "recordVersion") record[mutation] = 2;
            else if (mutation == "payload") record[mutation]!["fields"]!["status"] = "UNREGISTERED_PROSE_SENTINEL";
            else record[mutation] = "UNREGISTERED_PROSE_SENTINEL";
            output["text"] = begin + record.ToJsonString() + end;
        }
        var input = (JsonObject)turn.Input[0]!.DeepClone();
        input["id"] = "question-1"; input["type"] = "message"; input["status"] = "completed";
        var answer = new JsonArray(new JsonObject
        {
            ["text"] = evidence,
            ["witnesses"] = new JsonArray(new JsonObject { ["documentId"] = "document-1", ["page"] = 1, ["quote"] = evidence }),
            ["sourceWitnesses"] = new JsonArray(new JsonObject { ["sourceId"] = "technical", ["recordId"] = "tech-airworthiness-001", ["recordVersion"] = 1 })
        }).ToJsonString();
        var history = new JsonArray(input,
            new JsonObject { ["id"] = "fc-1", ["type"] = "function_call", ["call_id"] = "call-1", ["name"] = SourceReadTools.GetTool, ["arguments"] = arguments.ToJsonString() },
            new JsonObject { ["id"] = "out-1", ["type"] = "function_call_output", ["call_id"] = "call-1", ["output"] = output.ToJsonString() },
            new JsonObject
            {
                ["id"] = "answer-1", ["type"] = "output_message", ["status"] = "completed", ["role"] = "assistant",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = answer })
            });
        var replay = Create();
        if (mutation is null)
        {
            _ = ManagedHistoryContract.Validate(history.ToJsonString(), replay.Policy);
            var claim = Assert.Single(replay.Claims!);
            Assert.Equal(evidence, claim.Text);
            Assert.Equal(new SourceRecordWitness("technical", "tech-airworthiness-001", 1), Assert.Single(claim.VerifiedSourceBasis!));
            Assert.Equal(1, source.PageReads);
        }
        else
        {
            Assert.Equal("conversation_history_invalid", Assert.Throws<ManagedProjectException>(() =>
                ManagedHistoryContract.Validate(history.ToJsonString(), replay.Policy)).Code);
            Assert.Equal(0, source.InventoryReads);
            Assert.Equal(0, source.PageReads);
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"documentId\":\"document-1\"}")]
    [InlineData("{\"documentId\":\"document-1\",\"page\":\"1\"}")]
    [InlineData("{\"documentId\":\"document-1\",\"page\":1,\"caseId\":\"foreign-case\"}")]
    [InlineData("{\"documentId\":{\"raw\":\"sentinel\"},\"page\":1}")]
    public async Task ManagedInvestigatorArguments_RejectBeforeRealCaseRead(string json)
    {
        const string evidence = "SYNTHETIC EVIDENCE: aircraft evidence v1.";
        var source = new ManagedClaimSource(evidence);
        var turn = new ManagedInvestigatorTurn("cp-case-a", "technical-reviewer", source,
            new(new TechnicalSource(), new MaintenanceSource(), new FinanceSource(), new RecordsSource(), new ContractsSource()),
            FakeApprovedManifest.Approved(), new("evidence-v1", "swedencentral", evidence), SyntheticContentRegistry.FromCommittedFixtures());
        var args = JsonNode.Parse(json)!.AsObject();
        Assert.False(turn.Policy.Arguments(CaseReadTools.SourcePageTool, args));
        Assert.Equal("conversation_history_invalid", (await Assert.ThrowsAsync<ManagedProjectException>(() =>
            turn.DispatchAsync(new("call-1", CaseReadTools.SourcePageTool, args), TestContext.Current.CancellationToken))).Code);
        Assert.Equal(0, source.InventoryReads);
        Assert.Equal(0, source.PageReads);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("case")]
    [InlineData("source")]
    [InlineData("raw")]
    [InlineData("note")]
    [InlineData("receipt")]
    public void ManagedCopilotPolicy_UsesRealArgumentProjectionAndAnswerValidators(string? mutation)
    {
        var result = new JsonObject
        {
            ["contractVersion"] = 1, ["httpStatus"] = 200,
            ["result"] = new JsonObject
            {
                ["caseId"] = "CASE-01", ["sourceId"] = "technical", ["outcome"] = "ok",
                ["refs"] = new JsonArray(new JsonObject { ["sourceId"] = "technical", ["recordId"] = "record-1", ["recordVersion"] = 1 })
            }
        };
        var name = mutation == "receipt" ? "advance_stage" : "read_source";
        var arguments = mutation == "receipt" ? "{\"caseId\":\"CASE-01\",\"expectedRevision\":0}" :
            "{\"caseId\":\"CASE-01\",\"sourceId\":\"technical\",\"operation\":\"record\",\"recordId\":\"record-1\"}";
        switch (mutation)
        {
            case "case": result["result"]!["caseId"] = "FOREIGN"; break;
            case "source": result["result"]!["sourceId"] = "finance"; break;
            case "raw": result["result"]!["rawText"] = "RAW_SENTINEL"; break;
            case "note": result["uiOnly"] = new JsonObject { ["note"] = "NOTE_SENTINEL" }; break;
            case "receipt": result["result"] = new JsonObject { ["receiptId"] = "RECEIPT_SENTINEL" }; break;
        }
        var items = new JsonArray(
            Message("user-1", "user", "What did the case record say?"),
            new JsonObject { ["id"] = "fc-1", ["type"] = "function_call", ["call_id"] = "call-1", ["name"] = name, ["arguments"] = arguments },
            new JsonObject { ["id"] = "out-1", ["type"] = "function_call_output", ["call_id"] = "call-1", ["output"] = result.ToJsonString() },
            Message("assistant-1", "assistant", "{\"answer\":\"Record available.\",\"citations\":[{\"sourceId\":\"technical\",\"recordId\":\"record-1\",\"recordVersion\":1}]}"));
        if (mutation is null)
        {
            var policy = ManagedHistoryContract.CopilotPolicy("CASE-01");
            Assert.Equal(items.ToJsonString(), ManagedHistoryContract.Validate(items.ToJsonString(), policy).Json);
            Assert.Equal("conversation_history_invalid", Assert.Throws<ManagedProjectException>(() =>
                ManagedHistoryContract.Validate(new JsonArray(items[^1]!.DeepClone()).ToJsonString(), policy)).Code);
            items.Add(Message("user-2", "user", "What did the case record say?"));
            var previousAnswer = items[3]!.DeepClone();
            previousAnswer["id"] = "assistant-2";
            items.Add(previousAnswer);
            Assert.Equal("conversation_history_invalid", Assert.Throws<ManagedProjectException>(() =>
                ManagedHistoryContract.Validate(items.ToJsonString(), policy)).Code);
        }
        else Assert.Equal("conversation_history_invalid", Assert.Throws<ManagedProjectException>(() =>
            ManagedHistoryContract.Validate(items.ToJsonString(), ManagedHistoryContract.CopilotPolicy("CASE-01"))).Code);
    }

    [Theory]
    [InlineData("{\"answer\":\"ok\",\"citations\":[]}", true)]
    [InlineData("{\"answer\":\"ok\",\"extra\":\"RAW_SENTINEL\"}", false)]
    [InlineData("{\"answer\":\"ok\",\"citations\":[{\"sourceId\":\"technical\",\"recordId\":\"record-1\",\"recordVersion\":1}]}", false)]
    public void ManagedCopilotPolicy_RejectsUnreturnedCitationsAndUnknownAnswerFields(string answer, bool accepted)
    {
        var items = new JsonArray(Message("user-1", "user", "Question"), Message("answer-1", "assistant", answer));
        if (accepted)
            Assert.Equal(items.ToJsonString(), ManagedHistoryContract.Validate(items.ToJsonString(), ManagedHistoryContract.CopilotPolicy("CASE-01")).Json);
        else Assert.Equal("conversation_history_invalid", Assert.Throws<ManagedProjectException>(() =>
            ManagedHistoryContract.Validate(items.ToJsonString(), ManagedHistoryContract.CopilotPolicy("CASE-01"))).Code);
    }

    [Theory]
    [InlineData("get_case_summary", "{\"caseId\":\"CASE-01\"}", true)]
    [InlineData("get_case_summary", "{\"caseId\":\"FOREIGN\"}", false)]
    [InlineData("get_case_summary", "{\"caseId\":\"CASE-01\",\"extra\":\"RAW_SENTINEL\"}", false)]
    [InlineData("list_my_cases", "{}", true)]
    [InlineData("list_my_cases", "{\"caseId\":\"CASE-01\"}", false)]
    [InlineData("unknown", "{}", false)]
    public void ManagedCopilotPolicy_UsesNativeClosedToolArgumentContract(string name, string arguments, bool accepted)
    {
        var items = new JsonArray(new JsonObject
        {
            ["id"] = "fc-1", ["type"] = "function_call", ["call_id"] = "call-1", ["name"] = name, ["arguments"] = arguments
        });
        if (accepted)
            Assert.Equal(items.ToJsonString(), ManagedHistoryContract.Validate(items.ToJsonString(), ManagedHistoryContract.CopilotPolicy("CASE-01")).Json);
        else Assert.Equal("conversation_history_invalid", Assert.Throws<ManagedProjectException>(() =>
            ManagedHistoryContract.Validate(items.ToJsonString(), ManagedHistoryContract.CopilotPolicy("CASE-01"))).Code);
    }

    [Theory]
    [InlineData(1000, 1000, true)]
    [InlineData(1000, 1001, false)]
    public void ManagedHistory_QuestionScalarLimitAppliesAcrossContentParts(int first, int second, bool accepted)
    {
        var items = new JsonArray(Message("user-1", "user", new string('a', first), new string('b', second)));
        if (accepted)
            Assert.Equal(items.ToJsonString(), ManagedHistoryContract.Validate(items.ToJsonString(), ManagedHistoryContract.CopilotPolicy(null)).Json);
        else Assert.Equal("conversation_history_invalid", Assert.Throws<ManagedProjectException>(() =>
            ManagedHistoryContract.Validate(items.ToJsonString(), ManagedHistoryContract.CopilotPolicy(null))).Code);
    }

    private static JsonObject Message(string id, string role, params string[] parts) => new()
    {
        ["id"] = id, ["type"] = "message", ["status"] = "completed", ["role"] = role,
        ["content"] = new JsonArray([.. parts.Select(text => (JsonNode)new JsonObject
        {
            ["type"] = role == "user" ? "input_text" : "output_text", ["text"] = text
        })])
    };
}

using System.Text.Json.Nodes;
using AirlineDemo.Deployment;

namespace AirlineDemo.Api.Tests;

public sealed class ManagedCreateRecoveryTests
{
    [Fact]
    public async Task ManagedCreateRecovery_UsesExactReservationAndAttemptAcrossAllPages()
    {
        var cursors = new List<string?>();
        var admitted = 0;
        var result = await ManagedCreateRecovery.ResolveConversationAsync("reservation-1", "attempt-1", (after, _) =>
        {
            cursors.Add(after);
            return Task.FromResult(after is null
                ? Page(true, Item("unrelated", "reservation-1", "other"), Item("candidate", "reservation-1", "attempt-1"))
                : Page(false, Item("other", "different", "attempt-1")));
        }, () => ++admitted <= 3, TestContext.Current.CancellationToken);
        Assert.Equal("candidate", result);
        Assert.Equal([null, "candidate"], cursors);
        Assert.Equal(2, admitted);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("multiple")]
    [InlineData("unsupported")]
    [InlineData("incomplete")]
    [InlineData("repeated")]
    [InlineData("malformed-metadata")]
    public async Task ManagedCreateRecovery_AmbiguousOrUnsupportedEnumerationStaysUnresolved(string scenario)
    {
        var calls = 0;
        var result = await ManagedCreateRecovery.ResolveConversationAsync("reservation-1", "attempt-1", (_, _) =>
        {
            calls++;
            var candidate = Item("candidate", "reservation-1", "attempt-1");
            return Task.FromResult(scenario switch
            {
                "none" => Page(false, Item("other", "reservation-1", "different")),
                "multiple" => Page(false, candidate, Item("second", "reservation-1", "attempt-1")),
                "unsupported" => new JsonObject { ["data"] = new JsonArray(candidate) },
                "incomplete" => new JsonObject { ["data"] = new JsonArray(candidate), ["has_more"] = true },
                "repeated" => Page(true, candidate),
                _ => Page(false, new JsonObject { ["id"] = "candidate", ["metadata"] = new JsonObject { ["reservation_id"] = 1 } })
            });
        }, () => true, TestContext.Current.CancellationToken);
        Assert.Null(result);
        Assert.Equal(scenario == "repeated" ? 2 : 1, calls);
    }

    [Fact]
    public async Task ManagedCreateRecovery_RequestBudgetCannotBecomeSuccessfulPartialEnumeration()
    {
        var calls = 0;
        var admissions = 0;
        var result = await ManagedCreateRecovery.ResolveConversationAsync("reservation-1", "attempt-1", (_, _) =>
        {
            calls++;
            return Task.FromResult(Page(true, Item("candidate", "reservation-1", "attempt-1")));
        }, () => ++admissions <= 1, TestContext.Current.CancellationToken);
        Assert.Null(result);
        Assert.Equal((1, 2), (calls, admissions));
    }

    private static JsonObject Item(string id, string reservation, string attempt) => new()
    {
        ["id"] = id, ["metadata"] = new JsonObject { ["reservation_id"] = reservation, ["attempt_id"] = attempt }
    };

    private static JsonObject Page(bool more, params JsonObject[] items) => new()
    {
        ["data"] = new JsonArray(items.Select(item => (JsonNode)item).ToArray()),
        ["has_more"] = more, ["last_id"] = items.LastOrDefault()?["id"]?.DeepClone()
    };
}

public sealed class ManagedAgentReconciliationTests
{
    private const string AgentName = "airline-copilot";

    private enum CreateFault { None, LostReply, NotStored, MalformedReply, AlteredStored }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ManagedAgentReconciliation_TwoIdenticalRunsCreateOneVersionAndAdoptTheSameManifest()
    {
        var edge = new FakeVersions();
        var ledger = new FakeLedger();
        var reconciler = new ManagedAgentDefinitionReconciler(edge, ledger);
        var first = await reconciler.ReconcileAsync(Spec(), null, Ct);
        var second = await reconciler.ReconcileAsync(Spec(), null, Ct);
        Assert.Equal(first, second);
        Assert.Equal(("1", 1, 2, ManagedAgentAttemptState.Completed), (first.AgentVersion, edge.Creates, ledger.Writes, ledger.State!.State));
        Assert.Equal(first, ManagedAgentReleaseManifest.Verify(first.ToJson()));
    }

    [Fact]
    public async Task ManagedAgentReconciliation_ForeignConfigurationIsSkippedAndNeverAdopted()
    {
        var edge = new FakeVersions();
        var reconciler = new ManagedAgentDefinitionReconciler(edge, new FakeLedger());
        var first = await reconciler.ReconcileAsync(Spec(modelVersion: "2026-01-01"), null, Ct);
        var second = await reconciler.ReconcileAsync(Spec(modelVersion: "2026-02-01"), null, Ct);
        Assert.Equal((2, "2"), (edge.Creates, second.AgentVersion));
        Assert.Equal(first.DefinitionDigest, second.DefinitionDigest);
        Assert.NotEqual(first.ConfigurationId, second.ConfigurationId);
        Assert.Equal(first, await reconciler.ReconcileAsync(Spec(modelVersion: "2026-01-01"), null, Ct));
        var fresh = new FakeLedger();
        Assert.Equal(first, await new ManagedAgentDefinitionReconciler(edge, fresh).ReconcileAsync(Spec(modelVersion: "2026-01-01"), null, Ct));
        Assert.Equal((2, 0), (edge.Creates, fresh.Writes));
    }

    [Fact]
    public async Task ManagedAgentReconciliation_PinnedExactVersionIsVerifiedWithoutLedgerOrCreate()
    {
        var edge = new FakeVersions();
        var ledger = new FakeLedger();
        var created = await new ManagedAgentDefinitionReconciler(edge, ledger).ReconcileAsync(Spec(), null, Ct);
        var (loads, writes) = (ledger.Loads, ledger.Writes);
        Assert.Equal(created, await new ManagedAgentDefinitionReconciler(edge, ledger).ReconcileAsync(Spec(), "1", Ct));
        Assert.Equal((1, loads, writes), (edge.Creates, ledger.Loads, ledger.Writes));
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("01")]
    [InlineData("0")]
    [InlineData("")]
    public async Task ManagedAgentReconciliation_PinnedVersionMustBeExactAndNeverFollowsLatest(string version)
    {
        var edge = new FakeVersions();
        var ledger = new FakeLedger();
        Assert.Equal("agent_definition_invalid", await CodeAsync(() => new ManagedAgentDefinitionReconciler(edge, ledger).ReconcileAsync(Spec(), version, Ct)));
        Assert.Equal((0, 0, 0, 0), (edge.Reads + edge.Lists, edge.Creates, ledger.Loads, ledger.Writes));
    }

    [Fact]
    public async Task ManagedAgentReconciliation_MissingPinnedVersionIsReportedNotCreated()
    {
        var edge = new FakeVersions();
        var reconciler = new ManagedAgentDefinitionReconciler(edge, new FakeLedger());
        await reconciler.ReconcileAsync(Spec(), null, Ct);
        Assert.Equal("agent_version_missing", await CodeAsync(() => reconciler.ReconcileAsync(Spec(), "2", Ct)));
        Assert.Equal(1, edge.Creates);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("2")]
    public async Task ManagedAgentReconciliation_SameLabelsWithChangedDefinitionIsMismatchNotOverwritten(string? pinned)
    {
        var edge = new FakeVersions();
        var reconciler = new ManagedAgentDefinitionReconciler(edge, new FakeLedger());
        await reconciler.ReconcileAsync(Spec(), null, Ct);
        var altered = Part(edge.LastRequest!, "definition");
        altered["instructions"] = "Answer from memory.";
        edge.Store(altered, Part(edge.LastRequest!, "metadata"));
        Assert.Equal("agent_definition_mismatch", await CodeAsync(() => reconciler.ReconcileAsync(Spec(), pinned, Ct)));
        Assert.Equal(1, edge.Creates);
    }

    [Theory]
    [InlineData("lost-reply")]
    [InlineData("malformed-reply")]
    public async Task ManagedAgentReconciliation_StoredButUnconfirmedCreateIsAdoptedOnNextRun(string scenario)
    {
        var edge = new FakeVersions { Fault = scenario == "lost-reply" ? CreateFault.LostReply : CreateFault.MalformedReply };
        var ledger = new FakeLedger();
        var reconciler = new ManagedAgentDefinitionReconciler(edge, ledger);
        Assert.Equal("agent_reconciliation_required", await CodeAsync(() => reconciler.ReconcileAsync(Spec(), null, Ct)));
        Assert.Equal(ManagedAgentAttemptState.ReconciliationRequired, ledger.State!.State);
        edge.Fault = CreateFault.None;
        Assert.Equal("1", (await reconciler.ReconcileAsync(Spec(), null, Ct)).AgentVersion);
        Assert.Equal((1, ManagedAgentAttemptState.Completed), (edge.Creates, ledger.State!.State));
    }

    [Fact]
    public async Task ManagedAgentReconciliation_UnstoredUnknownCreateBlocksEveryRerunWithoutResubmitting()
    {
        var edge = new FakeVersions { Fault = CreateFault.NotStored };
        var ledger = new FakeLedger();
        var reconciler = new ManagedAgentDefinitionReconciler(edge, ledger);
        Assert.Equal("agent_reconciliation_required", await CodeAsync(() => reconciler.ReconcileAsync(Spec(), null, Ct)));
        edge.Fault = CreateFault.None;
        Assert.Equal("agent_reconciliation_required", await CodeAsync(() => reconciler.ReconcileAsync(Spec(), null, Ct)));
        Assert.Equal("agent_reconciliation_required", await CodeAsync(() => reconciler.ReconcileAsync(Spec(), null, Ct)));
        Assert.Equal((1, ManagedAgentAttemptState.ReconciliationRequired), (edge.Creates, ledger.State!.State));
    }

    [Fact]
    public async Task ManagedAgentReconciliation_AlteredStoredVersionAfterUnknownReplyIsMismatch()
    {
        var edge = new FakeVersions { Fault = CreateFault.AlteredStored };
        var ledger = new FakeLedger();
        var reconciler = new ManagedAgentDefinitionReconciler(edge, ledger);
        Assert.Equal("agent_reconciliation_required", await CodeAsync(() => reconciler.ReconcileAsync(Spec(), null, Ct)));
        edge.Fault = CreateFault.None;
        Assert.Equal("agent_definition_mismatch", await CodeAsync(() => reconciler.ReconcileAsync(Spec(), null, Ct)));
        Assert.Equal((1, ManagedAgentAttemptState.ReconciliationRequired), (edge.Creates, ledger.State!.State));
    }

    [Theory]
    [InlineData("load")]
    [InlineData("first-write")]
    [InlineData("race")]
    public async Task ManagedAgentReconciliation_LedgerFailureOrConflictStopsBeforeAnyCreate(string scenario)
    {
        var edge = new FakeVersions();
        var ledger = new FakeLedger();
        switch (scenario)
        {
            case "load": ledger.LoadFails = true; break;
            case "first-write": ledger.FailWriteNumber = 1; break;
            case "race":
                ledger.BeforeWrite = () => ledger.State = new ManagedAgentAttempt(ManagedAgentAttemptState.Attempted, "other-config", "other-digest");
                break;
        }
        Assert.Equal("agent_attempt_persistence_failed", await CodeAsync(() => new ManagedAgentDefinitionReconciler(edge, ledger).ReconcileAsync(Spec(), null, Ct)));
        Assert.Equal(0, edge.Creates);
    }

    [Fact]
    public async Task ManagedAgentReconciliation_RejectedCompletedWriteIsAdoptedWithoutSecondCreate()
    {
        var edge = new FakeVersions();
        var ledger = new FakeLedger { FailWriteNumber = 2 };
        var reconciler = new ManagedAgentDefinitionReconciler(edge, ledger);
        Assert.Equal("agent_attempt_persistence_failed", await CodeAsync(() => reconciler.ReconcileAsync(Spec(), null, Ct)));
        Assert.Equal(ManagedAgentAttemptState.Attempted, ledger.State!.State);
        Assert.Equal("1", (await reconciler.ReconcileAsync(Spec(), null, Ct)).AgentVersion);
        Assert.Equal((1, ManagedAgentAttemptState.Completed), (edge.Creates, ledger.State!.State));
    }

    [Fact]
    public async Task ManagedAgentReconciliation_TwoExactVersionsAreAmbiguousAndNeverChosen()
    {
        var edge = new FakeVersions();
        var reconciler = new ManagedAgentDefinitionReconciler(edge, new FakeLedger());
        await reconciler.ReconcileAsync(Spec(), null, Ct);
        edge.Store(Part(edge.LastRequest!, "definition"), Part(edge.LastRequest!, "metadata"));
        Assert.Equal("agent_identity_ambiguous", await CodeAsync(() => reconciler.ReconcileAsync(Spec(), null, Ct)));
        Assert.Equal(1, edge.Creates);
    }

    [Fact]
    public async Task ManagedAgentReconciliation_ListedRecordWithoutMetadataIsMalformed()
    {
        var edge = new FakeVersions();
        edge.Add(new JsonObject
        {
            ["object"] = "agent.version", ["id"] = "airline-copilot-1", ["name"] = AgentName, ["version"] = "1",
            ["created_at"] = 1_700_000_000L, ["definition"] = new JsonObject(),
        });
        var ledger = new FakeLedger();
        Assert.Equal("agent_version_malformed", await CodeAsync(() => new ManagedAgentDefinitionReconciler(edge, ledger).ReconcileAsync(Spec(), null, Ct)));
        Assert.Equal((0, 0), (edge.Creates, ledger.Writes));
    }

    [Theory]
    [InlineData("non-array-data")]
    [InlineData("too-many-items")]
    [InlineData("empty-id")]
    [InlineData("duplicate-id")]
    [InlineData("leading-zero-version")]
    [InlineData("string-has-more")]
    [InlineData("more-on-empty-page")]
    [InlineData("last-id-mismatch")]
    [InlineData("listed-but-unstored")]
    public async Task ManagedAgentReconciliation_IncompleteOrAmbiguousEnumerationFailsClosed(string scenario)
    {
        var edge = new FakeVersions { ListOverride = _ => BadPage(scenario) };
        var ledger = new FakeLedger();
        Assert.Equal("agent_enumeration_incomplete", await CodeAsync(() => new ManagedAgentDefinitionReconciler(edge, ledger).ReconcileAsync(Spec(), null, Ct)));
        Assert.Equal((0, 0), (edge.Creates, ledger.Writes));
    }

    [Fact]
    public async Task ManagedAgentReconciliation_ListFailureFailsClosedWithoutCreateOrWrite()
    {
        var edge = new FakeVersions { ListOverride = _ => throw new HttpRequestException("list unavailable") };
        var ledger = new FakeLedger();
        Assert.Equal("agent_enumeration_incomplete", await CodeAsync(() => new ManagedAgentDefinitionReconciler(edge, ledger).ReconcileAsync(Spec(), null, Ct)));
        Assert.Equal((0, 0), (edge.Creates, ledger.Writes));
    }

    [Fact]
    public async Task ManagedAgentReconciliation_ReadFailureAfterCreateFailsClosedWithoutSecondCreate()
    {
        var edge = new FakeVersions();
        var ledger = new FakeLedger();
        var reconciler = new ManagedAgentDefinitionReconciler(edge, ledger);
        await reconciler.ReconcileAsync(Spec(), null, Ct);
        edge.ReadFails = true;
        Assert.Equal("agent_enumeration_incomplete", await CodeAsync(() => reconciler.ReconcileAsync(Spec(), null, Ct)));
        Assert.Equal((1, 2), (edge.Creates, ledger.Writes));
    }

    [Fact]
    public async Task ManagedAgentReconciliation_RepeatedOrEndlessCursorStopsAtTheBound()
    {
        var repeated = new FakeVersions { ListOverride = _ => Page(true, Listed("a", "1")) };
        var pages = 0;
        var endless = new FakeVersions { ListOverride = _ => Page(true, Listed($"page-{++pages}", "1")) };
        Assert.Equal("agent_enumeration_incomplete", await CodeAsync(() => new ManagedAgentDefinitionReconciler(repeated, new FakeLedger()).ReconcileAsync(Spec(), null, Ct)));
        Assert.Equal("agent_enumeration_incomplete", await CodeAsync(() => new ManagedAgentDefinitionReconciler(endless, new FakeLedger()).ReconcileAsync(Spec(), null, Ct)));
        Assert.Equal((2, 10), (repeated.Lists, endless.Lists));
    }

    [Theory]
    [InlineData("instructions-with-escapes")]
    [InlineData("tools-and-text")]
    public async Task ManagedAgentReleaseManifest_VerifyAcceptsExactBytesForEscapedContent(string scenario)
    {
        var spec = scenario == "instructions-with-escapes"
            ? Spec(instructions: "Quote ' <b> & + é and \"x\".")
            : Spec() with { Tools = new JsonArray(Tool("lookup_case"), Tool("find_flight")) };
        var manifest = await new ManagedAgentDefinitionReconciler(new FakeVersions(), new FakeLedger()).ReconcileAsync(spec, null, Ct);
        Assert.Equal(manifest, ManagedAgentReleaseManifest.Verify(manifest.ToJson()));
    }

    [Theory]
    [InlineData("agent-version-latest")]
    [InlineData("agent-version-leading-zero")]
    [InlineData("configuration-id")]
    [InlineData("definition-digest")]
    [InlineData("instructions")]
    [InlineData("extra-field")]
    [InlineData("key-order")]
    [InlineData("pretty-printed")]
    public async Task ManagedAgentReleaseManifest_VerifyRejectsAlteredOrNoncanonicalBytes(string scenario)
    {
        var manifest = await new ManagedAgentDefinitionReconciler(new FakeVersions(), new FakeLedger()).ReconcileAsync(Spec(), null, Ct);
        var error = Assert.Throws<ManagedAgentReconciliationException>(() => ManagedAgentReleaseManifest.Verify(Tamper(manifest.ToJson(), scenario)));
        Assert.Equal("agent_manifest_invalid", error.Code);
    }

    [Theory]
    [InlineData("agent-name-leading-digit", "agent_definition_invalid")]
    [InlineData("model-version-latest", "agent_definition_invalid")]
    [InlineData("blank-instructions", "agent_definition_invalid")]
    [InlineData("tool-extra-key", "agent_definition_unsupported")]
    [InlineData("tool-type", "agent_definition_unsupported")]
    [InlineData("tool-duplicate-name", "agent_definition_unsupported")]
    [InlineData("tool-name-with-space", "agent_definition_unsupported")]
    [InlineData("tool-parameters-array", "agent_definition_unsupported")]
    public async Task ManagedAgentReconciliation_RefusesInvalidDefinitionBeforeAnyServiceOrLedgerCall(string scenario, string code)
    {
        var edge = new FakeVersions();
        var ledger = new FakeLedger();
        Assert.Equal(code, await CodeAsync(() => new ManagedAgentDefinitionReconciler(edge, ledger).ReconcileAsync(Variant(scenario), null, Ct)));
        Assert.Equal((0, 0, 0, 0), (edge.Reads + edge.Lists, edge.Creates, ledger.Loads, ledger.Writes));
    }

    [Fact]
    public async Task ManagedAgentSpecifications_DefinitionIdentityMatchesTheSeamForEachWorkload()
    {
        var config = new ManagedAgentSetupConfiguration(new Uri("https://demo.services.ai.azure.com/api/projects/airline-demo"),
            "airline-demo", "11111111-2222-3333-4444-555555555555", "runtime", "model-a", "2026-01-01", "demo-medium-high",
            "release-manifest.json", "state");
        var specifications = ManagedAgentSetup.Specifications(config);
        Assert.Equal(new[] { "copilot", "investigator" }, specifications.Select(item => item.Workload));
        foreach (var expected in specifications)
        {
            var edge = new FakeVersions();
            var seam = new ManagedAgentDefinitionSpec(expected.Workload, expected.Name, config.ProjectName,
                expected.Definition["instructions"]!.GetValue<string>(),
                (JsonArray)expected.Definition["tools"]!.DeepClone(),
                (JsonObject)expected.Definition["text"]!.DeepClone(),
                new ManagedAgentModel(expected.ModelName, expected.ModelDeployment, expected.ModelVersion, expected.FilterPolicy),
                new ManagedAgentSdkPins(expected.DotnetVersion, expected.AzureIdentityVersion), expected.Description);
            var manifest = await new ManagedAgentDefinitionReconciler(edge, new FakeLedger()).ReconcileAsync(seam, null, Ct);
            Assert.Equal(expected.DefinitionSha256, manifest.DefinitionDigest);
            Assert.Equal(expected.DefinitionSha256, edge.LastRequest!["metadata"]!["definition_sha256"]!.GetValue<string>());
        }
    }

    private static ManagedAgentDefinitionSpec Spec(string instructions = "Answer only from the supplied case.", string modelVersion = "2026-01-01") =>
        new("copilot", AgentName, "airline-demo", instructions, new JsonArray(Tool("lookup_case")),
            new JsonObject { ["format"] = new JsonObject { ["type"] = "text" } },
            new ManagedAgentModel("model-a", "runtime", modelVersion, "demo-medium-high"), new ManagedAgentSdkPins("net10.0", "1.0.0"));

    private static ManagedAgentDefinitionSpec Variant(string scenario)
    {
        var spec = Spec();
        var strict = Tool("lookup_case");
        strict["strict"] = true;
        var typed = Tool("lookup_case");
        typed["type"] = "code_interpreter";
        var arrayParameters = Tool("lookup_case");
        arrayParameters["parameters"] = new JsonArray();
        return scenario switch
        {
            "agent-name-leading-digit" => spec with { AgentName = "1airline-copilot" },
            "model-version-latest" => spec with { Model = spec.Model with { Version = "latest" } },
            "blank-instructions" => spec with { Instructions = " " },
            "tool-extra-key" => spec with { Tools = new JsonArray(strict) },
            "tool-type" => spec with { Tools = new JsonArray(typed) },
            "tool-duplicate-name" => spec with { Tools = new JsonArray(Tool("lookup_case"), Tool("LOOKUP_CASE")) },
            "tool-name-with-space" => spec with { Tools = new JsonArray(Tool("lookup case")) },
            "tool-parameters-array" => spec with { Tools = new JsonArray(arrayParameters) },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
    }

    private static JsonObject Tool(string name) => new()
    {
        ["type"] = "function",
        ["name"] = name,
        ["description"] = "Reads one airline case.",
        ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false },
    };

    private static JsonObject Listed(string id, string version) => new() { ["id"] = id, ["version"] = version };

    private static JsonObject Page(bool more, params JsonObject[] items) => new()
    {
        ["data"] = new JsonArray(items.Select(item => (JsonNode)item).ToArray()),
        ["has_more"] = more,
        ["last_id"] = items.LastOrDefault()?["id"]?.DeepClone(),
    };

    private static JsonObject BadPage(string scenario) => scenario switch
    {
        "non-array-data" => new JsonObject { ["data"] = "nope", ["has_more"] = false },
        "too-many-items" => Page(false, Enumerable.Range(0, 101).Select(index => Listed($"id-{index}", $"{index + 1}")).ToArray()),
        "empty-id" => Page(false, Listed("", "1")),
        "duplicate-id" => Page(false, Listed("a", "1"), Listed("a", "2")),
        "leading-zero-version" => Page(false, Listed("a", "01")),
        "string-has-more" => new JsonObject { ["data"] = new JsonArray(), ["has_more"] = "false" },
        "more-on-empty-page" => Page(true),
        "last-id-mismatch" => new JsonObject { ["data"] = new JsonArray(Listed("a", "1")), ["has_more"] = true, ["last_id"] = "b" },
        "listed-but-unstored" => Page(false, Listed("a", "5")),
        _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
    };

    private static JsonObject Part(JsonObject request, string name) => (JsonObject)request[name]!.DeepClone();

    private static string Tamper(string json, string scenario)
    {
        var fields = JsonNode.Parse(json)!.AsObject();
        switch (scenario)
        {
            case "agent-version-latest": fields["agentVersion"] = "latest"; break;
            case "agent-version-leading-zero": fields["agentVersion"] = "01"; break;
            case "configuration-id": fields["configurationId"] = new string('0', 64); break;
            case "definition-digest": fields["definitionDigest"] = new string('0', 64); break;
            case "instructions": fields["instructions"] = "Answer without the case."; break;
            case "extra-field": fields["extra"] = "x"; break;
            case "key-order":
                var reversed = new JsonObject();
                foreach (var key in fields.Select(pair => pair.Key).Reverse().ToArray()) reversed[key] = fields[key]!.DeepClone();
                return reversed.ToJsonString();
            case "pretty-printed":
                return fields.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }
        return fields.ToJsonString();
    }

    private static async Task<string> CodeAsync(Func<Task> action)
    {
        try
        {
            await action();
            return "succeeded";
        }
        catch (ManagedAgentReconciliationException exception)
        {
            return exception.Code;
        }
    }

    private sealed class FakeVersions : IManagedAgentVersionEdge
    {
        private readonly List<JsonObject> _records = [];

        public int Reads { get; private set; }
        public int Lists { get; private set; }
        public int Creates { get; private set; }
        public JsonObject? LastRequest { get; private set; }
        public CreateFault Fault { get; set; }
        public bool ReadFails { get; set; }
        public Func<string?, JsonObject>? ListOverride { get; set; }

        public Task<JsonObject?> ReadAsync(string agentName, string version, CancellationToken cancellationToken)
        {
            Reads++;
            if (ReadFails) throw new HttpRequestException("read unavailable");
            var record = _records.FirstOrDefault(item => item["version"]?.GetValue<string>() == version);
            return Task.FromResult<JsonObject?>(record is null ? null : (JsonObject)record.DeepClone());
        }

        public Task<JsonObject> ListPageAsync(string agentName, string? after, int limit, CancellationToken cancellationToken)
        {
            Lists++;
            if (ListOverride is not null) return Task.FromResult(ListOverride(after));
            var start = after is null ? 0 : _records.FindIndex(item => item["id"]?.GetValue<string>() == after) + 1;
            var page = _records.Skip(start).Take(limit).ToArray();
            return Task.FromResult(new JsonObject
            {
                ["data"] = new JsonArray(page.Select(item => (JsonNode)new JsonObject
                {
                    ["id"] = item["id"]!.DeepClone(),
                    ["version"] = item["version"]!.DeepClone(),
                }).ToArray()),
                ["has_more"] = start + page.Length < _records.Count,
                ["last_id"] = page.LastOrDefault()?["id"]?.DeepClone(),
            });
        }

        public Task<JsonObject> CreateAsync(string agentName, JsonObject request, CancellationToken cancellationToken)
        {
            Creates++;
            LastRequest = (JsonObject)request.DeepClone();
            if (Fault == CreateFault.NotStored) throw new HttpRequestException("lost before the service stored the version");
            var definition = Part(request, "definition");
            if (Fault == CreateFault.AlteredStored) definition["instructions"] = "Changed by the service.";
            var reply = Store(definition, Part(request, "metadata"), agentName);
            switch (Fault)
            {
                case CreateFault.LostReply: throw new HttpRequestException("reply lost after the service stored the version");
                case CreateFault.MalformedReply: reply.Remove("id"); break;
            }
            return Task.FromResult(reply);
        }

        public JsonObject Store(JsonObject definition, JsonObject metadata, string agentName = AgentName)
        {
            var version = (_records.Count + 1).ToString();
            var record = new JsonObject
            {
                ["object"] = "agent.version",
                ["id"] = $"{agentName}-{version}",
                ["name"] = agentName,
                ["version"] = version,
                ["created_at"] = 1_700_000_000L + _records.Count,
                ["definition"] = definition,
                ["metadata"] = metadata,
            };
            _records.Add(record);
            return (JsonObject)record.DeepClone();
        }

        public void Add(JsonObject record) => _records.Add(record);
    }

    private sealed class FakeLedger : IManagedAgentAttemptLedger
    {
        public ManagedAgentAttempt? State { get; set; }
        public int Loads { get; private set; }
        public int Writes { get; private set; }
        public int FailWriteNumber { get; set; }
        public bool LoadFails { get; set; }
        public Action? BeforeWrite { get; set; }

        public Task<ManagedAgentAttempt?> LoadAsync(string agentName, CancellationToken cancellationToken)
        {
            Loads++;
            if (LoadFails) throw new InvalidOperationException("ledger unavailable");
            return Task.FromResult(State);
        }

        public Task RecordAsync(string agentName, ManagedAgentAttempt? expected, ManagedAgentAttempt next, CancellationToken cancellationToken)
        {
            Writes++;
            BeforeWrite?.Invoke();
            if (Writes == FailWriteNumber) throw new InvalidOperationException("write rejected");
            if (!object.Equals(State, expected)) throw new InvalidOperationException("compare-and-set conflict");
            State = next;
            return Task.CompletedTask;
        }
    }
}

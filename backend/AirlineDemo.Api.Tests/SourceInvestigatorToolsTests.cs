using System.Text.Json;
using AirlineDemo.Api.Investigation;
using AirlineDemo.Api.SourceSystems;
using Microsoft.Extensions.AI;

namespace AirlineDemo.Api.Tests;

public sealed class SourceInvestigatorToolsTests
{
    private const string CaseId = "bound-case";
    private const string Region = "swedencentral";
    private const string Evidence = "SYNTHETIC EVIDENCE: Return readiness is pending reserve approval.";
    private const string Claims =
        """[{"text":"Return readiness is pending reserve approval.","witnesses":[{"documentId":"return-status","page":1,"quote":"Return readiness is pending reserve approval."}]}]""";
    private const string ApprovedJson =
        """{"manifestVersion":"2026.10.1","approved":true,"arm":"investigation-live","deploymentName":"fixture-deployment","model":"fixture-model","apiVersion":"2025-01-01","region":"swedencentral","contentFilter":"enabled"}""";
    private static readonly string[] SourceIds = ["technical", "maintenance", "finance", "records", "contracts"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed class CountingLimits : IInvestigationLimits
    {
        public int Tools { get; private set; }
        public int Models { get; private set; }
        public List<int> InputEstimates { get; } = [];

        public ValueTask BeforeToolCallAsync(CancellationToken cancellationToken)
        {
            Tools++;
            return ValueTask.CompletedTask;
        }

        public ValueTask BeforeModelCallAsync(CancellationToken cancellationToken)
        {
            Models++;
            return ValueTask.CompletedTask;
        }

        public ValueTask BeforeModelCallAsync(int estimatedInputTokens, CancellationToken cancellationToken)
        {
            Models++;
            InputEstimates.Add(estimatedInputTokens);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingSource<TPayload>(SourceRecord<TPayload> record)
        : ISourceSystem<TPayload>, ICountingSource where TPayload : class
    {
        public int ListCalls { get; private set; }
        public int GetCalls { get; private set; }
        public string? LastListCase { get; private set; }
        public string? LastGetCase { get; private set; }
        public string? LastRecordId { get; private set; }
        public SourceRecord<TPayload> Record { get; } = record;
        object ICountingSource.RecordValue => Record;

        public Task<SourceReadResult<IReadOnlyList<SourceRecord<TPayload>>>> ListAsync(
            string caseId, CancellationToken cancellationToken = default, int? maxRecords = null)
        {
            ListCalls++;
            LastListCase = caseId;
            IReadOnlyList<SourceRecord<TPayload>> result = record.CaseId == caseId ? [record] : [];
            return Task.FromResult<SourceReadResult<IReadOnlyList<SourceRecord<TPayload>>>>(
                new SourceReadSucceeded<IReadOnlyList<SourceRecord<TPayload>>>(result));
        }

        public Task<SourceReadResult<SourceRecord<TPayload>>> GetAsync(
            string caseId, string recordId, CancellationToken cancellationToken = default)
        {
            GetCalls++;
            LastGetCase = caseId;
            LastRecordId = recordId;
            SourceReadResult<SourceRecord<TPayload>> result = record.CaseId == caseId && record.RecordId == recordId
                ? new SourceReadSucceeded<SourceRecord<TPayload>>(record)
                : new SourceRecordNotFound<SourceRecord<TPayload>>(recordId);
            return Task.FromResult(result);
        }
    }

    private interface ICountingSource
    {
        int ListCalls { get; }
        int GetCalls { get; }
        string? LastListCase { get; }
        string? LastGetCase { get; }
        string? LastRecordId { get; }
        object RecordValue { get; }
    }

    private sealed record Sources(
        CountingSource<TechnicalPayload> Technical,
        CountingSource<MaintenancePayload> Maintenance,
        CountingSource<FinancePayload> Finance,
        CountingSource<RecordsPayload> Records,
        CountingSource<ContractsPayload> Contracts)
    {
        public InvestigationSourceSystems Ports => new(Technical, Maintenance, Finance, Records, Contracts);
    }

    private sealed class CaseSource(string pageText = "Return readiness is pending reserve approval.") : ICaseReadSource
    {
        public IReadOnlyList<string> GetInventory(string caseId) => ["return-status"];
        public string? GetApprovedRequirements(string caseId) => null;
        public string? GetSourcePage(string caseId, string documentId, int page) => page == 1 ? pageText : null;
        public IReadOnlyList<string> SearchWithinBasis(string caseId, string query) => [];
        public string? GetProcessingStatus(string caseId) => null;
    }

    private sealed class ScriptedChatClient(Func<IList<ChatMessage>, ChatOptions?, ChatResponse> respond) : IChatClient
    {
        public List<IList<ChatMessage>> Requests { get; } = [];
        public List<ChatOptions?> Options { get; } = [];
        public ChatClientMetadata Metadata { get; } = new("source-tools-test");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var request = messages.ToList();
            Requests.Add(request);
            Options.Add(options);
            return Task.FromResult(respond(request, options));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static ApprovedModelManifest Approved() => ApprovedModelManifest.Parse(ApprovedJson);
    private static ChatResponse Reply(string text) => new(new ChatMessage(ChatRole.Assistant, text));

    private static Sources CreateSources(string payload = "synthetic source value", string caseId = CaseId) => new(
        Source<TechnicalPayload>("technical", payload, caseId), Source<MaintenancePayload>("maintenance", payload, caseId),
        Source<FinancePayload>("finance", payload, caseId), Source<RecordsPayload>("records", payload, caseId),
        Source<ContractsPayload>("contracts", payload, caseId));

    private static CountingSource<TPayload> Source<TPayload>(string sourceId, string value, string caseId)
        where TPayload : class
    {
        var fields = new Dictionary<string, string> { ["value"] = value };
        if (typeof(TPayload) == typeof(RecordsPayload))
        {
            fields.Add("documentId", "return-status");
            fields.Add("documentVersion", "1");
            fields.Add("containsPersonalData", "false");
        }
        object payload = typeof(TPayload) == typeof(TechnicalPayload) ? new TechnicalPayload(fields)
            : typeof(TPayload) == typeof(MaintenancePayload) ? new MaintenancePayload(fields)
            : typeof(TPayload) == typeof(FinancePayload) ? new FinancePayload(fields)
            : typeof(TPayload) == typeof(RecordsPayload) ? new RecordsPayload(fields)
            : typeof(TPayload) == typeof(ContractsPayload) ? new ContractsPayload(fields)
            : throw new InvalidOperationException($"Unknown source payload {typeof(TPayload).Name}.");
        var record = new SourceRecord<TPayload>
        {
            SourceId = sourceId, RecordId = "record-1", RecordVersion = 1, AsOf = new DateOnly(2026, 10, 1),
            CaseId = caseId, Kind = "test", Payload = (TPayload)payload,
        };
        return new CountingSource<TPayload>(record);
    }

    private static string Expected<TPayload>(TPayload value) =>
        $"{BoundedInvestigator.UntrustedBegin}\n{JsonSerializer.Serialize(value, JsonOptions)}\n{BoundedInvestigator.UntrustedEnd}";

    private static ChatResponse Calls(params FunctionCallContent[] calls) =>
        new(new ChatMessage(ChatRole.Assistant, calls));

    private static string CompleteRequestText(ScriptedChatClient model) =>
        string.Join("\n", model.Requests.SelectMany(request => request)
            .SelectMany(message => message.Contents)
            .Select(content => content switch
            {
                TextContent text => text.Text ?? string.Empty,
                FunctionResultContent result => result.Result?.ToString() ?? string.Empty,
                _ => content.ToString() ?? string.Empty,
            }));

    private static string CompleteSerializedRequest(ScriptedChatClient model) =>
        JsonSerializer.Serialize(new
        {
            messages = model.Requests.Select(request => request.Select(message => new
            {
                role = message.Role.ToString(),
                contents = message.Contents.Select(content =>
                    JsonSerializer.Serialize(content, content.GetType(), AIJsonUtilities.DefaultOptions)).ToArray(),
            })),
            options = model.Options.Select(option => new
            {
                instructions = option?.Instructions,
                tools = option?.Tools?.Select(tool => new
                {
                    tool.Name,
                    tool.Description,
                    schema = tool is AIFunctionDeclaration declaration ? declaration.JsonSchema.GetRawText() : null,
                }).ToArray(),
            }),
        }, JsonOptions);

    [Fact]
    public async Task InjectionAndUnpinnedCitationCannotExpandInvestigatorAuthority()
    {
        const string caseId = "case-001";
        var sources = CreateSources(caseId: caseId);
        var seeded = Assert.IsType<SourceReadSucceeded<SourceRecord<MaintenancePayload>>>(
            await new MaintenanceSource().GetAsync(caseId, "mx-task-card-001"));
        var taskCard = seeded.Value;
        var maintenance = new CountingSource<MaintenancePayload>(taskCard);
        var ports = new InvestigationSourceSystems(sources.Technical, maintenance, sources.Finance, sources.Records, sources.Contracts);
        var unauthorizedNames = new[] { "set_source_mode", "fetch_url", "approve", "send_to_recipient", "create_mock_request", "accept" };
        var calls = 0;
        var model = new ScriptedChatClient((_, _) => ++calls switch
        {
            1 => Calls(
                new FunctionCallContent("task-card", SourceReadTools.GetTool,
                    new Dictionary<string, object?> { ["sourceId"] = "maintenance", ["recordId"] = taskCard.RecordId }),
                new FunctionCallContent("page", CaseReadTools.SourcePageTool,
                    new Dictionary<string, object?> { ["documentId"] = "return-status", ["page"] = 1 })),
            2 => Calls(
                unauthorizedNames.Select((name, index) =>
                    new FunctionCallContent($"unauthorized-{index}", name, new Dictionary<string, object?>())).Append(
                    new FunctionCallContent("out-of-case", SourceReadTools.GetTool,
                        new Dictionary<string, object?>
                        {
                            ["sourceId"] = "other-case",
                            ["recordId"] = taskCard.RecordId,
                            ["caseId"] = "another-case",
                        })).ToArray()),
            _ => Reply(
                """[{"text":"Return readiness is pending reserve approval.","witnesses":[{"documentId":"return-status","page":1,"quote":"Return readiness is pending reserve approval."}],"sourceWitnesses":[{"sourceId":"maintenance","recordId":"not-read-record","recordVersion":1}]}]"""),
        });
        var investigator = new BoundedInvestigator(caseId, new CaseSource(Evidence), Approved(),
            SyntheticContentRegistry.FromCommittedFixtures(), Region, "evidence-v1", model,
            role: "technical-reviewer", sourceSystems: ports);

        var error = await Record.ExceptionAsync(() => investigator.InvestigateAsync([Evidence]));

        Assert.IsType<InvestigationOutputException>(error);
        Assert.Equal("source record witness is not in the current basis", error!.Message);
        var declaredNames = model.Options[0]!.Tools!.Select(tool => tool.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[]
        {
            CaseReadTools.InventoryTool,
            CaseReadTools.RequirementsTool,
            CaseReadTools.SourcePageTool,
            CaseReadTools.SearchTool,
            CaseReadTools.StatusTool,
            SourceReadTools.GetTool,
            SourceReadTools.ListTool,
        }.Order(StringComparer.Ordinal).ToArray(), declaredNames);
        var requestText = CompleteRequestText(model);
        Assert.Contains(Expected(taskCard), requestText, StringComparison.Ordinal);
        Assert.Contains(
            $"{BoundedInvestigator.UntrustedBegin}\n{Evidence}\n{BoundedInvestigator.UntrustedEnd}",
            requestText,
            StringComparison.Ordinal);
        Assert.Equal((caseId, taskCard.RecordId, 1), (maintenance.LastGetCase, maintenance.LastRecordId, maintenance.GetCalls));
        Assert.Equal(0, sources.Technical.ListCalls + sources.Technical.GetCalls
            + maintenance.ListCalls + sources.Finance.ListCalls + sources.Finance.GetCalls
            + sources.Records.ListCalls + sources.Records.GetCalls + sources.Contracts.ListCalls + sources.Contracts.GetCalls);
    }

    [Theory]
    [InlineData("technical", "record-1", 1, ClaimStatus.Supported, null, false)]
    [InlineData("maintenance", "record-1", 1, ClaimStatus.Downgraded, "source record witness is not in the current basis", false)]
    [InlineData("technical", "record-2", 1, ClaimStatus.Downgraded, "source record witness is not in the current basis", false)]
    [InlineData("technical", "record-1", 2, ClaimStatus.Downgraded, "source record witness is not in the current basis", false)]
    [InlineData(null, null, 0, ClaimStatus.Downgraded, "no source record witness", false)]
    [InlineData(null, null, 0, ClaimStatus.Downgraded, "no source record witness", true)]
    public void SourceClaimsRequireExactPinnedTuple(
        string? sourceId,
        string? recordId,
        int recordVersion,
        ClaimStatus expectedStatus,
        string? expectedReason,
        bool omitSourceWitnesses)
    {
        var pinned = new SourceRecordWitness("technical", "record-1", 1);
        IReadOnlyList<SourceRecordWitness>? witnesses = omitSourceWitnesses
            ? null
            : sourceId is null
                ? []
                : [new SourceRecordWitness(sourceId, recordId!, recordVersion)];
        var claim = new InvestigationClaim(
            "approved",
            [new DocumentWitness("document-1", 1, "approved")],
            witnesses);
        var result = ClaimValidator.Validate(
            claim,
            ClaimKind.Baseline,
            new Dictionary<string, BasisDocument>(StringComparer.Ordinal)
            {
                ["document-1"] = BasisDocument.Create("document-1", ["approved"]),
            },
            "approved",
            [pinned]);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(expectedReason, result.Reason);
    }

    [Fact]
    public async Task MarkedFieldsStayOutOfModelAndSourceClaims()
    {
        const string caseId = "case-001";
        const string recordId = "rec-doc-arr-0012";
        var sources = CreateSources(caseId: caseId);
        var markedRecord = new SourceRecord<RecordsPayload>
        {
            SourceId = "records",
            RecordId = recordId,
            RecordVersion = 1,
            AsOf = new DateOnly(2026, 10, 1),
            CaseId = caseId,
            Kind = "document_index",
            Payload = new RecordsPayload(new Dictionary<string, string>
            {
                ["documentId"] = "SYN-DOC0012",
                ["documentVersion"] = "1",
                ["containsPersonalData"] = "true",
                ["subjectName"] = "SYN-PERSON-NAME-01",
                ["licenseNumber"] = "SYN-LICENSE-NUMBER-01",
            }),
        };
        var records = new CountingSource<RecordsPayload>(markedRecord);
        var ports = new InvestigationSourceSystems(sources.Technical, sources.Maintenance, sources.Finance, records, sources.Contracts);
        var calls = 0;
        var model = new ScriptedChatClient((_, _) => ++calls == 1
            ? Calls(new FunctionCallContent("marked-record", SourceReadTools.GetTool,
                new Dictionary<string, object?> { ["sourceId"] = "records", ["recordId"] = recordId }))
            : Reply(
                """[{"text":"Return readiness is pending reserve approval.","witnesses":[{"documentId":"return-status","page":1,"quote":"Return readiness is pending reserve approval."}],"sourceWitnesses":[{"sourceId":"records","recordId":"rec-doc-arr-0012","recordVersion":1}]}]"""));
        var investigator = new BoundedInvestigator(caseId, new CaseSource(), Approved(),
            SyntheticContentRegistry.FromCommittedFixtures(), Region, "evidence-v1", model,
            role: "technical-reviewer", sourceSystems: ports);

        var result = await investigator.InvestigateAsync([Evidence]);

        var completeRequest = CompleteSerializedRequest(model);
        var completeClaims = JsonSerializer.Serialize(result.Claims, JsonOptions);
        foreach (var sensitive in new[] { "subjectName", "licenseNumber", "SYN-PERSON-NAME-01", "SYN-LICENSE-NUMBER-01" })
        {
            Assert.DoesNotContain(sensitive, completeRequest, StringComparison.Ordinal);
            Assert.DoesNotContain(sensitive, completeClaims, StringComparison.Ordinal);
        }

        Assert.Contains(Expected(markedRecord), CompleteRequestText(model), StringComparison.Ordinal);
        Assert.Equal((1, caseId, recordId), (records.GetCalls, records.LastGetCase, records.LastRecordId));
    }

    [Theory]
    [InlineData("subjectName", false)]
    [InlineData("licenseNumber", false)]
    [InlineData("SYN-PERSON-NAME-01", false)]
    [InlineData("SYN-LICENSE-NUMBER-01", false)]
    [InlineData("syn-person-name-01", false)]
    [InlineData("syn-license-number-01", false)]
    [InlineData("SYN PERSON NAME 01", true)]
    [InlineData("SYN.PERSON.NAME.01", true)]
    [InlineData("SYN_PERSON_NAME_01", true)]
    public async Task MarkedPersonalDataIsRejectedFromReturnedClaims(string markedText, bool isCitation)
    {
        const string caseId = "case-001";
        const string recordId = "rec-doc-arr-0012";
        var sources = CreateSources(caseId: caseId);
        var markedRecord = new SourceRecord<RecordsPayload>
        {
            SourceId = "records",
            RecordId = recordId,
            RecordVersion = 1,
            AsOf = new DateOnly(2026, 10, 1),
            CaseId = caseId,
            Kind = "document_index",
            Payload = new RecordsPayload(new Dictionary<string, string>
            {
                ["documentId"] = "SYN-DOC0012",
                ["documentVersion"] = "1",
                ["containsPersonalData"] = "true",
                ["subjectName"] = "SYN-PERSON-NAME-01",
                ["licenseNumber"] = "SYN-LICENSE-NUMBER-01",
            }),
        };
        var records = new CountingSource<RecordsPayload>(markedRecord);
        var ports = new InvestigationSourceSystems(sources.Technical, sources.Maintenance, sources.Finance, records, sources.Contracts);
        var claim = JsonSerializer.Serialize(new[]
        {
            new
            {
                text = isCitation ? "Return readiness is pending reserve approval." : markedText,
                witnesses = new[]
                {
                    new
                    {
                        documentId = "return-status",
                        page = 1,
                        quote = isCitation ? markedText : "Return readiness is pending reserve approval.",
                    },
                },
                sourceWitnesses = new[] { new { sourceId = "records", recordId, recordVersion = 1 } },
            },
        }, JsonOptions);
        var calls = 0;
        var model = new ScriptedChatClient((_, _) => ++calls == 1
            ? Calls(new FunctionCallContent("marked-record", SourceReadTools.GetTool,
                new Dictionary<string, object?> { ["sourceId"] = "records", ["recordId"] = recordId }))
            : Reply(claim));
        var investigator = new BoundedInvestigator(caseId, new CaseSource(), Approved(),
            SyntheticContentRegistry.FromCommittedFixtures(), Region, "evidence-v1", model,
            role: "technical-reviewer", sourceSystems: ports);

        var error = await Record.ExceptionAsync(() => investigator.InvestigateAsync([Evidence]));

        Assert.IsType<InvestigationOutputException>(error);
        Assert.Equal("claim contains marked personal data", error!.Message);
    }

    [Fact]
    public async Task SourceToolAllowlistHasOnlyTwoCaseBoundReadOperations()
    {
        var sources = CreateSources();
        var model = new ScriptedChatClient((_, _) => Reply(Claims));
        var investigator = new BoundedInvestigator(CaseId, new CaseSource(), Approved(),
            SyntheticContentRegistry.FromCommittedFixtures(), Region, "evidence-v1", model,
            role: "technical-reviewer", sourceSystems: sources.Ports);

        await investigator.InvestigateAsync([Evidence]);

        var tools = model.Options[0]!.Tools!;
        var sourceTools = tools.Where(tool => tool.Name is SourceReadTools.ListTool or SourceReadTools.GetTool)
            .OfType<AIFunctionDeclaration>().ToDictionary(tool => tool.Name, tool => JsonDocument.Parse(tool.JsonSchema.GetRawText()).RootElement);
        Assert.Equal(new[] { SourceReadTools.GetTool, SourceReadTools.ListTool }, sourceTools.Keys.Order(StringComparer.Ordinal).ToArray());
        var listSchema = sourceTools[SourceReadTools.ListTool];
        var getSchema = sourceTools[SourceReadTools.GetTool];
        Assert.Equal(["sourceId"], listSchema.GetProperty("properties").EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(["recordId", "sourceId"], getSchema.GetProperty("properties").EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task SourceToolsEnforceRoleAndBoundCaseForEveryMatrixPair()
    {
        var roles = new[] { "technical-reviewer", "finance-reviewer", "legal-reviewer", "presenter", "executive", "unknown" };
        foreach (var role in roles)
        foreach (var sourceId in SourceIds)
        {
            var sources = CreateSources();
            var limits = new CountingLimits();
            var tools = new SourceReadTools(CaseId, role, sources.Ports, limits);
            var list = tools.Functions.Single(function => function.Name == SourceReadTools.ListTool);
            var get = tools.Functions.Single(function => function.Name == SourceReadTools.GetTool);

            var listResult = (await list.InvokeAsync(new AIFunctionArguments { ["sourceId"] = sourceId }))?.ToString();
            var getResult = (await get.InvokeAsync(new AIFunctionArguments
            {
                ["sourceId"] = sourceId,
                ["recordId"] = "record-1",
            }))?.ToString();

            var allowed = role switch
            {
                "technical-reviewer" => sourceId is "technical" or "maintenance" or "records",
                "finance-reviewer" => sourceId is "technical" or "finance",
                "legal-reviewer" => sourceId is "contracts" or "records",
                _ => false,
            };
            ICountingSource selected = sourceId switch
            {
                "technical" => sources.Technical,
                "maintenance" => sources.Maintenance,
                "finance" => sources.Finance,
                "records" => sources.Records,
                "contracts" => sources.Contracts,
                _ => throw new InvalidOperationException("Unexpected source identifier."),
            };

            if (allowed)
            {
                Assert.Equal(Expected(new[] { selected.RecordValue }), listResult);
                Assert.Equal(Expected(selected.RecordValue), getResult);
                Assert.Equal((CaseId, CaseId, "record-1"), (selected.LastListCase, selected.LastGetCase, selected.LastRecordId));
            }
            else
            {
                Assert.Equal("""{"error":"forbidden"}""", listResult);
                Assert.Equal("""{"error":"forbidden"}""", getResult);
                Assert.Equal((0, 0), (selected.ListCalls, selected.GetCalls));
            }

            Assert.Equal(2, limits.Tools);
            Assert.Equal(0, sources.Technical.ListCalls + sources.Maintenance.ListCalls + sources.Finance.ListCalls
                + sources.Records.ListCalls + sources.Contracts.ListCalls - (allowed ? 1 : 0));
            Assert.Equal(0, sources.Technical.GetCalls + sources.Maintenance.GetCalls + sources.Finance.GetCalls
                + sources.Records.GetCalls + sources.Contracts.GetCalls - (allowed ? 1 : 0));
        }
    }

    [Fact]
    public async Task SourceCallsUseExistingInvestigationBudgets()
    {
        var longValue = "source payload " + "x".PadRight(3000, 'x');
        var sources = CreateSources(longValue);
        var limits = new CountingLimits();
        var deniedTools = new SourceReadTools(CaseId, "presenter", sources.Ports, limits);
        var denied = await deniedTools.Functions.Single(function => function.Name == SourceReadTools.ListTool)
            .InvokeAsync(new AIFunctionArguments { ["sourceId"] = "technical" });
        Assert.Equal("""{"error":"forbidden"}""", denied?.ToString());
        Assert.Equal(1, limits.Tools);
        Assert.Equal(0, sources.Technical.ListCalls);

        var model = new ScriptedChatClient((messages, _) =>
            messages.Any(message => message.Contents.OfType<FunctionResultContent>().Any())
                ? Reply(
                    """[{"text":"Return readiness is pending reserve approval.","witnesses":[{"documentId":"return-status","page":1,"quote":"Return readiness is pending reserve approval."}],"sourceWitnesses":[{"sourceId":"technical","recordId":"record-1","recordVersion":1}]}]""")
                : new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("source-list", SourceReadTools.ListTool, new Dictionary<string, object?> { ["sourceId"] = "technical" })])));
        var investigator = new BoundedInvestigator(CaseId, new CaseSource(), Approved(),
            SyntheticContentRegistry.FromCommittedFixtures(), Region, "evidence-v1", model, limits,
            role: "technical-reviewer", sourceSystems: sources.Ports);

        await investigator.InvestigateAsync([Evidence]);

        Assert.Equal(2, limits.Tools);
        Assert.Equal(2, limits.Models);
        Assert.Equal((1, 0), (sources.Technical.ListCalls, sources.Technical.GetCalls));
        Assert.True(limits.InputEstimates[1] > limits.InputEstimates[0] + 1000);
    }
}

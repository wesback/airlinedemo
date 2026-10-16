using System.Text.Json;
using AirlineDemo.Api.Investigation;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Policy;
using AirlineDemo.Api.Requests;

namespace AirlineDemo.Api.Tests;

/// <summary>Covers FR-002, FR-003, FR-005, FR-013, NFR-001 and AC-002, AC-007, AC-017 for the assessment service.</summary>
public sealed class AssessmentExecutionServiceTests : IDisposable
{
    private const string Quote = "Return readiness is pending reserve approval.";
    private const string Value = "pending reserve approval";
    private const string ScenarioTime = "2026-10-14T00:00:00Z";
    private const string BasisId = "basis-7";
    private const int BasisVersion = 3;

    private static readonly ProcessingCoverage Complete = new(Inventory: 2, Processed: 2, Failed: 0, Partial: 0);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"assessment-{Guid.NewGuid():N}.json");
    private readonly CaseStateStore _store;
    private readonly CaseScope _scope;
    private readonly MockRequestInbox _inbox = new();
    private readonly AssessmentStore _assessments = new();
    private readonly AssessmentExecutionService _service;

    public AssessmentExecutionServiceTests()
    {
        _store = new CaseStateStore(_path);
        _scope = _store.CreateCase("case-1", "presenter");
        _service = new AssessmentExecutionService(_store, _inbox, _assessments);
        _assessments.SetCurrentBasis("case-1", CurrentBasis());
    }

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    private static IReadOnlyDictionary<string, BasisDocument> Documents() => new Dictionary<string, BasisDocument>
    {
        ["return-status"] = BasisDocument.Create("return-status", [Quote, "Page two text."]),
    };

    private static AssessmentBasis CurrentBasis(
        ProcessingCoverage? coverage = null,
        bool ambiguous = false,
        IReadOnlyDictionary<string, BasisDocument>? documents = null,
        int version = BasisVersion) =>
        new(BasisId, version, ScenarioTime, coverage ?? Complete, ambiguous, documents ?? Documents());

    private static AssessmentClaim Missing(string quote = Quote, string docId = "return-status", string value = Value) =>
        new(ClaimKind.MissingRequirement, value, "Engine", "ENG-123", [new DocumentWitness(docId, 1, quote)]);

    // AC-01: a single supported missing requirement with the exact typed value on the current basis creates one request.
    [Fact]
    public void Supported_single_missing_requirement_creates_one_mock_request_with_current_basis()
    {
        var record = _service.Assess(_scope, CurrentBasis(), [Missing()]);

        Assert.Equal("Request", record.Outcome);
        Assert.NotNull(record.RequestId);
        Assert.Null(record.WorkItemId);
        var request = Assert.Single(_inbox.Requests);
        Assert.Equal(record.RequestId, request.RequestId);
        Assert.Equal("case-1", request.CaseId);
        Assert.Equal(BasisId, request.BasisId);
        Assert.Equal(BasisVersion, request.BasisVersion);
        Assert.Equal("assessment-3", request.ReportId);
        var expected = new DeliveryEnvelope(
            "case-1:basis-7:3:assessment", "case-1", "assessment-3", BasisId, BasisVersion, ScenarioTime, "Engine", "ENG-123", Value);
        Assert.Equal(MockRequestTemplate.Render(expected, record.RequestId!), request.Body);
        Assert.Empty(_store.ListWorkItems(_scope));
    }

    // AC-01 negative: the typed value is absent from the cited quote, so the claim is downgraded and no request is created.
    [Fact]
    public void Claim_whose_typed_value_is_absent_from_its_quote_creates_no_request()
    {
        var record = _service.Assess(_scope, CurrentBasis(), [Missing(quote: "Return readiness is pending")]);

        Assert.Equal("Review", record.Outcome);
        Assert.Equal("downgraded claim: claim value not in witness quote", record.Reason);
        Assert.Null(record.RequestId);
        Assert.Empty(_inbox.Requests);
    }

    // AC-02: a supported baseline creates no request and no review item; a missing requirement does not take that outcome.
    [Fact]
    public void Baseline_creates_no_request()
    {
        var baseline = new AssessmentClaim(ClaimKind.Baseline, Value, "Engine", "ENG-123", [new DocumentWitness("return-status", 1, Quote)]);

        var record = _service.Assess(_scope, CurrentBasis(), [baseline]);

        Assert.Equal("Baseline", record.Outcome);
        Assert.Null(record.RequestId);
        Assert.Null(record.WorkItemId);
        Assert.Empty(_inbox.Requests);
        Assert.Empty(_store.ListWorkItems(_scope));

        var missing = _service.Assess(_scope, CurrentBasis(), [Missing()]);
        Assert.NotEqual("Baseline", missing.Outcome);
    }

    // The stored basis is a copy: the caller changing the evidence it passed in does not change what is validated.
    [Fact]
    public void Stored_basis_is_not_changed_by_later_edits_to_the_supplied_evidence()
    {
        var pages = new List<string> { Quote, "Page two text." };
        var documents = new Dictionary<string, BasisDocument> { ["return-status"] = BasisDocument.Create("return-status", pages) };
        var basis = CurrentBasis(documents: documents);
        _assessments.SetCurrentBasis("case-1", basis);

        pages[0] = "Return readiness is pending";
        documents["return-status"] = BasisDocument.Create("return-status", ["Replaced text."]);
        documents["other"] = BasisDocument.Create("other", ["Extra."]);

        var stored = _assessments.CurrentBasis("case-1")!;
        Assert.Equal([Quote, "Page two text."], stored.Documents["return-status"].Pages);
        Assert.False(stored.Documents.ContainsKey("other"));

        var original = _service.Assess(_scope, basis, [Missing()]);
        Assert.Equal("Request", original.Outcome);
        var edited = _service.Assess(_scope, basis, [Missing(quote: "Return readiness is pending")]);
        Assert.Equal("Review", edited.Outcome);
        Assert.Null(edited.RequestId);
    }

    // AC-03: ambiguity, conflict, incomplete coverage and a downgraded witness each open one review item and no request.
    [Fact]
    public void Ambiguous_or_unsupported_claim_creates_review_item_not_request()
    {
        var conflict = new AssessmentClaim(ClaimKind.Conflict, Value, "Engine", "ENG-123", [new DocumentWitness("return-status", 1, Quote)]);
        var incomplete = new ProcessingCoverage(2, 1, 0, 0);
        var scenarios = new (AssessmentBasis Stored, AssessmentBasis Supplied, AssessmentClaim[] Claims)[]
        {
            (CurrentBasis(ambiguous: true), CurrentBasis(ambiguous: true), [Missing()]),
            (CurrentBasis(), CurrentBasis(), [conflict]),
            (CurrentBasis(coverage: incomplete), CurrentBasis(coverage: incomplete), [Missing()]),
            (CurrentBasis(), CurrentBasis(), [Missing(docId: "history")]),
            (CurrentBasis(), CurrentBasis(version: BasisVersion - 1), [Missing()]),
            // A supplied complete basis cannot override the stored incomplete coverage of the current basis.
            (CurrentBasis(coverage: incomplete), CurrentBasis(), [Missing()]),
        };

        foreach (var (stored, supplied, claims) in scenarios)
        {
            _assessments.SetCurrentBasis("case-1", stored);
            var record = _service.Assess(_scope, supplied, claims);
            Assert.Equal("Review", record.Outcome);
            Assert.NotNull(record.WorkItemId);
            Assert.Null(record.RequestId);
        }

        var items = _store.ListWorkItems(_scope);
        Assert.Equal(scenarios.Length, items.Count);
        Assert.All(items, item =>
        {
            Assert.Equal("records-investigation", item.CapabilityId);
            Assert.Equal("open", item.State);
            Assert.Equal("technical-reviewer", item.Owner);
        });
        Assert.Empty(_inbox.Requests);

        // A clear supported missing requirement does not add a review item.
        _assessments.SetCurrentBasis("case-1", CurrentBasis());
        var clear = _service.Assess(_scope, CurrentBasis(), [Missing()]);
        Assert.Equal("Request", clear.Outcome);
        Assert.Equal(scenarios.Length, _store.ListWorkItems(_scope).Count);
    }

    // AC-04: stored results carry typed values and source ids only, never the quote, document text or prompt.
    [Fact]
    public void Assessment_record_exposes_typed_values_and_source_ids_only()
    {
        const string QuoteSentinel = "QUOTE-SENTINEL-77";
        const string DocumentSentinel = "DOC-TEXT-SENTINEL-55";
        const string PromptSentinel = "PROMPT-SENTINEL-99";
        var documents = new Dictionary<string, BasisDocument>
        {
            ["return-status"] = BasisDocument.Create("return-status", [$"{QuoteSentinel} {Value}", $"{DocumentSentinel} {PromptSentinel}"]),
        };

        _assessments.SetCurrentBasis("case-1", CurrentBasis(documents: documents));
        var record = _service.Assess(_scope, CurrentBasis(documents: documents), [Missing(quote: $"{QuoteSentinel} {Value}")]);

        var claim = Assert.Single(record.Claims);
        Assert.Equal("MissingRequirement", claim.Kind);
        Assert.Equal(Value, claim.Value);
        Assert.Equal("Engine", claim.Component);
        Assert.Equal("ENG-123", claim.SerialNumber);
        var witness = Assert.Single(claim.Witnesses);
        Assert.Equal("return-status", witness.DocumentId);
        Assert.Equal(1, witness.Page);

        var stored = Assert.Single(_assessments.List("case-1"));
        Assert.Equal(record, stored);
        var json = JsonSerializer.Serialize(stored, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.Contains("\"kind\"", json);
        Assert.Contains("\"value\"", json);
        Assert.Contains("\"component\"", json);
        Assert.Contains("\"serialNumber\"", json);
        Assert.Contains("\"documentId\"", json);
        Assert.Contains("\"page\"", json);
        Assert.DoesNotContain("\"quote\"", json);
        Assert.DoesNotContain(QuoteSentinel, json);
        Assert.DoesNotContain(DocumentSentinel, json);
        Assert.DoesNotContain(PromptSentinel, json);
    }
}

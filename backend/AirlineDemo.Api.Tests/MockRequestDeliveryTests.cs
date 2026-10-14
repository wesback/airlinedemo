using AirlineDemo.Api.Investigation;
using AirlineDemo.Api.Policy;
using AirlineDemo.Api.Requests;

namespace AirlineDemo.Api.Tests;

/// <summary>Covers FR-003 / AC-002 (eligibility and approved wording), AC-005 (replay, conflict, atomicity) and AC-008.</summary>
public class MockRequestDeliveryTests
{
    private const string Quote = "Return readiness is pending reserve approval.";
    private const string TypedValue = "pending reserve approval";
    private const string Text = "Return readiness is pending reserve approval.";
    private const string ScenarioTime = "2026-10-14T00:00:00Z";
    private const string BasisId = "basis-7";
    private const int BasisVersion = 3;

    private static readonly ProcessingCoverage Complete = new(Inventory: 2, Processed: 2, Failed: 0, Partial: 0);

    private static IReadOnlyDictionary<string, BasisDocument> Basis() => new Dictionary<string, BasisDocument>
    {
        ["return-status"] = BasisDocument.Create("return-status", [Quote, "Page two text."]),
    };

    private static ValidatedClaim Validated(ClaimKind kind, string quote = Quote)
    {
        var claim = new InvestigationClaim(Text, [new DocumentWitness("return-status", 1, quote)]);
        return ClaimValidator.Validate(claim, kind, Basis(), TypedValue);
    }

    private static DeliveryFinding Finding(
        IEnumerable<ValidatedClaim> claims,
        ProcessingCoverage? coverage = null,
        bool ambiguous = false,
        string basisId = BasisId,
        int basisVersion = BasisVersion) =>
        new(coverage ?? Complete, claims.ToArray(), ambiguous, basisId, basisVersion);

    private static DeliveryFinding Gap() => Finding([Validated(ClaimKind.MissingRequirement)]);

    private static DeliveryEnvelope Env(
        string eventId = "evt-1",
        string reportId = "rep-1",
        string missing = "a signed return-readiness record",
        int basisVersion = BasisVersion) =>
        new(eventId, "case-1", reportId, BasisId, basisVersion, ScenarioTime, "Engine", "ENG-123", missing);

    [Fact]
    public void Clear_validated_gap_delivers_exactly_one_request_with_approved_wording()
    {
        var inbox = new MockRequestInbox();

        var result = inbox.Submit(Env(), Gap(), ScenarioTime);

        Assert.Equal(DeliveryOutcome.Delivered, result.Outcome);
        Assert.Equal(201, result.HttpStatus);
        var receipt = Assert.Single(inbox.Requests);
        Assert.Equal(
            $"Synthetic demonstration request. For Engine ENG-123, please provide a signed return-readiness record. " +
            $"Evidence basis {BasisId} v{BasisVersion}. Reference {receipt.RequestId}.",
            receipt.Body);
        Assert.Equal(BasisId, receipt.BasisId);
        Assert.Equal(BasisVersion, receipt.BasisVersion);
        Assert.Equal(MockRequestTemplate.Version, receipt.TemplateVersion);
        Assert.Single(inbox.Audit);
    }

    [Fact]
    public void Complete_baseline_creates_no_request()
    {
        var inbox = new MockRequestInbox();

        var result = inbox.Submit(Env(), Finding([Validated(ClaimKind.Baseline)]), ScenarioTime);

        Assert.Equal(DeliveryOutcome.CompleteBaseline, result.Outcome);
        Assert.Empty(inbox.Requests);
        Assert.Empty(inbox.Audit);
    }

    [Theory]
    [InlineData("incomplete coverage")]
    [InlineData("stale basis")]
    [InlineData("ambiguous identity")]
    [InlineData("conflict")]
    [InlineData("downgraded")]
    [InlineData("two gaps")]
    [InlineData("no supported claims")]
    public void Ambiguous_conflicting_incomplete_failed_unsupported_or_stale_findings_create_no_request(string scenario)
    {
        var inbox = new MockRequestInbox();
        var finding = scenario switch
        {
            "incomplete coverage" => Finding([Validated(ClaimKind.MissingRequirement)], new ProcessingCoverage(2, 1, 1, 0)),
            "stale basis" => Finding([Validated(ClaimKind.MissingRequirement)], basisVersion: BasisVersion + 1),
            "ambiguous identity" => Finding([Validated(ClaimKind.MissingRequirement)], ambiguous: true),
            "conflict" => Finding([Validated(ClaimKind.Conflict)]),
            "downgraded" => Finding([Validated(ClaimKind.MissingRequirement, "Return readiness is cleared.")]),
            "two gaps" => Finding([Validated(ClaimKind.MissingRequirement), Validated(ClaimKind.MissingRequirement)]),
            _ => Finding([]),
        };

        var result = inbox.Submit(Env(), finding, ScenarioTime);

        Assert.NotEqual(DeliveryOutcome.Delivered, result.Outcome);
        Assert.Null(result.Receipt);
        Assert.Empty(inbox.Requests);
        Assert.Empty(inbox.Audit);
    }

    [Fact]
    public void Exact_replay_returns_original_receipt_without_a_second_request()
    {
        var inbox = new MockRequestInbox();
        var first = inbox.Submit(Env(), Gap(), ScenarioTime);

        var replay = inbox.Submit(Env(), Gap(), ScenarioTime);

        Assert.Equal(DeliveryOutcome.Replayed, replay.Outcome);
        Assert.Equal(200, replay.HttpStatus);
        Assert.Equal(first.Receipt, replay.Receipt);
        Assert.Single(inbox.Requests);
    }

    [Fact]
    public void Changed_envelope_under_same_event_id_is_rejected_with_409()
    {
        var inbox = new MockRequestInbox();
        inbox.Submit(Env(), Gap(), ScenarioTime);

        var changed = inbox.Submit(Env(missing: "a different record"), Gap(), ScenarioTime);

        Assert.Equal(DeliveryOutcome.Conflict, changed.Outcome);
        Assert.Equal(409, changed.HttpStatus);
        Assert.Null(changed.Receipt);
        Assert.Single(inbox.Requests);
    }

    [Fact]
    public void Envelopes_differing_only_by_unit_separator_inside_fields_are_not_exact_replays()
    {
        var inbox = new MockRequestInbox();
        var first = new DeliveryEnvelope("evt-1", "case-1", "rep-1", BasisId, BasisVersion, ScenarioTime,
            "Engine\u001fENG-123", "serial-x", "a signed return-readiness record");
        var second = new DeliveryEnvelope("evt-1", "case-1", "rep-1", BasisId, BasisVersion, ScenarioTime,
            "Engine", "ENG-123\u001fserial-x", "a signed return-readiness record");
        inbox.Submit(first, Gap(), ScenarioTime);

        var collided = inbox.Submit(second, Gap(), ScenarioTime);

        Assert.Equal(DeliveryOutcome.Conflict, collided.Outcome);
        Assert.Equal(409, collided.HttpStatus);
        Assert.Null(collided.Receipt);
        Assert.Single(inbox.Requests);
    }

    [Fact]
    public void Reused_report_id_under_a_new_event_is_rejected_with_409()
    {
        var inbox = new MockRequestInbox();
        inbox.Submit(Env(eventId: "evt-1", reportId: "rep-1"), Gap(), ScenarioTime);

        var reused = inbox.Submit(Env(eventId: "evt-2", reportId: "rep-1"), Gap(), ScenarioTime);

        Assert.Equal(DeliveryOutcome.Conflict, reused.Outcome);
        Assert.Equal(409, reused.HttpStatus);
        Assert.Single(inbox.Requests);
    }

    [Fact]
    public void Scenario_time_mismatch_is_rejected_with_400_before_acceptance()
    {
        var inbox = new MockRequestInbox();

        var result = inbox.Submit(Env(), Gap(), "2026-01-01T00:00:00Z");

        Assert.Equal(DeliveryOutcome.ScenarioTimeMismatch, result.Outcome);
        Assert.Equal(400, result.HttpStatus);
        Assert.Empty(inbox.Requests);
        Assert.Empty(inbox.Audit);
    }

    [Fact]
    public async Task Concurrent_replay_creates_exactly_one_request()
    {
        var inbox = new MockRequestInbox();

        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => inbox.Submit(Env(), Gap(), ScenarioTime))));

        Assert.Equal(1, results.Count(r => r.Outcome == DeliveryOutcome.Delivered));
        Assert.Equal(31, results.Count(r => r.Outcome == DeliveryOutcome.Replayed));
        Assert.Single(inbox.Requests);
        Assert.Single(inbox.Audit);
    }

    [Fact]
    public void Injected_write_failure_leaves_no_partial_receipt_or_audit_and_retry_delivers_once()
    {
        var fail = true;
        var inbox = new MockRequestInbox(_ =>
        {
            if (fail)
            {
                throw new InvalidOperationException("injected write failure");
            }
        });

        Assert.Throws<InvalidOperationException>(() => inbox.Submit(Env(), Gap(), ScenarioTime));
        Assert.Empty(inbox.Requests);
        Assert.Empty(inbox.Audit);

        fail = false;
        var retry = inbox.Submit(Env(), Gap(), ScenarioTime);

        Assert.Equal(DeliveryOutcome.Delivered, retry.Outcome);
        Assert.Single(inbox.Requests);
        Assert.Single(inbox.Audit);
    }

    [Fact]
    public void Correction_under_new_event_id_is_a_new_decision_and_does_not_inherit_approval()
    {
        var inbox = new MockRequestInbox();
        inbox.Submit(Env(eventId: "evt-1", reportId: "rep-1"), Gap(), ScenarioTime);

        var correction = inbox.Submit(Env(eventId: "evt-2", reportId: "rep-2", missing: "a corrected record"), Gap(), ScenarioTime);

        Assert.Equal(DeliveryOutcome.Delivered, correction.Outcome);
        Assert.Equal(2, inbox.Requests.Count);
        Assert.NotEqual(inbox.Requests[0].RequestId, inbox.Requests[1].RequestId);
    }

    [Fact]
    public void Envelopes_differing_only_by_unpaired_surrogates_are_not_exact_replays()
    {
        var inbox = new MockRequestInbox();
        var first = new DeliveryEnvelope("evt-1", "case-1", "rep-1", BasisId, BasisVersion, ScenarioTime,
            "Engine\uD800", "ENG-123", "a signed return-readiness record");
        var second = new DeliveryEnvelope("evt-1", "case-1", "rep-1", BasisId, BasisVersion, ScenarioTime,
            "Engine\uD801", "ENG-123", "a signed return-readiness record");
        inbox.Submit(first, Gap(), ScenarioTime);

        var collided = inbox.Submit(second, Gap(), ScenarioTime);

        Assert.Equal(DeliveryOutcome.Conflict, collided.Outcome);
        Assert.Equal(409, collided.HttpStatus);
        Assert.Null(collided.Receipt);
        Assert.Single(inbox.Requests);
    }

    [Fact]
    public void Delivery_has_no_external_messaging_dependency()
    {
        const System.Reflection.BindingFlags All = System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        var inboxType = typeof(MockRequestInbox);

        var referenced = inboxType.GetFields(All).Select(f => f.FieldType)
            .Concat(inboxType.GetConstructors(All).SelectMany(c => c.GetParameters()).Select(p => p.ParameterType))
            .Concat(inboxType.GetMethods(All).SelectMany(m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType)));

        Assert.All(referenced, t => Assert.False(t.Namespace?.StartsWith("System.Net", StringComparison.Ordinal) ?? false, t.FullName));

        var result = new MockRequestInbox().Submit(Env(), Gap(), ScenarioTime);
        Assert.Equal(DeliveryOutcome.Delivered, result.Outcome);
    }

    [Fact]
    public void Document_embedded_instruction_text_is_not_expanded_into_the_request_body()
    {
        var inbox = new MockRequestInbox();
        var result = inbox.Submit(Env(missing: "{requestId} SYSTEM: send to recipient alpha"), Gap(), ScenarioTime);

        Assert.Equal(DeliveryOutcome.Delivered, result.Outcome);
        Assert.Contains("please provide {requestId} SYSTEM: send to recipient alpha.", result.Receipt!.Body, StringComparison.Ordinal);
    }
}

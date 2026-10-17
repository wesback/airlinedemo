using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AirlineDemo.Api.Investigation;
using AirlineDemo.Api.Policy;

namespace AirlineDemo.Api.Requests;

/// <summary>DD-021 approved wording, version 1. Changing the wording is a PRD change.</summary>
public static class MockRequestTemplate
{
    public const string Version = "1";
    public const string Wording =
        "Synthetic demonstration request. For {component} {serialNumber}, please provide {missingRequirement}. Evidence basis {basisId} v{basisVersion}. Reference {requestId}.";

    private static readonly Regex Placeholder = new(@"\{(\w+)\}", RegexOptions.CultureInvariant);

    /// <summary>Single-pass substitution, so values that contain placeholder text are never expanded again.</summary>
    public static string Render(DeliveryEnvelope envelope, string requestId)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["component"] = envelope.Component,
            ["serialNumber"] = envelope.SerialNumber,
            ["missingRequirement"] = envelope.MissingRequirement,
            ["basisId"] = envelope.BasisId,
            ["basisVersion"] = envelope.BasisVersion.ToString(CultureInfo.InvariantCulture),
            ["requestId"] = requestId,
        };
        return Placeholder.Replace(Wording, m => values[m.Groups[1].Value]);
    }
}

/// <summary>The caller-supplied event. Its identity and fields are fingerprinted for exact-replay checks.</summary>
public sealed record DeliveryEnvelope(
    string EventId,
    string CaseId,
    string ReportId,
    string BasisId,
    int BasisVersion,
    string ScenarioTime,
    string Component,
    string SerialNumber,
    string MissingRequirement);

/// <summary>Validated findings for the current evidence basis.</summary>
public sealed record DeliveryFinding(
    ProcessingCoverage Coverage,
    IReadOnlyList<ValidatedClaim> Claims,
    bool IdentityAmbiguous,
    string CurrentBasisId,
    int CurrentBasisVersion);

public enum DeliveryDecisionKind
{
    Eligible,
    CompleteBaseline,
    Review,
    Blocked,
}

public sealed record DeliveryDecision(DeliveryDecisionKind Kind, string Reason);

/// <summary>
/// Deterministic eligibility for one routine request. Only one supported missing-requirement gap on the current basis,
/// with complete coverage, no ambiguity, no conflict and no unverified claim, is eligible.
/// </summary>
public static class MockDeliveryPolicy
{
    public static DeliveryDecision Evaluate(DeliveryFinding finding, DeliveryEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(finding);
        ArgumentNullException.ThrowIfNull(envelope);

        if (!finding.Coverage.IsComplete)
        {
            return new DeliveryDecision(DeliveryDecisionKind.Blocked, "processing or inventory coverage incomplete");
        }

        if (envelope.BasisId != finding.CurrentBasisId || envelope.BasisVersion != finding.CurrentBasisVersion)
        {
            return new DeliveryDecision(DeliveryDecisionKind.Review, "stale evidence basis");
        }

        if (finding.IdentityAmbiguous)
        {
            return new DeliveryDecision(DeliveryDecisionKind.Review, "ambiguous identity");
        }

        if (finding.Claims.Any(c => c.Status == ClaimStatus.Supported && c.Kind == ClaimKind.Conflict))
        {
            return new DeliveryDecision(DeliveryDecisionKind.Review, "conflicting evidence");
        }

        if (finding.Claims.Any(c => c.Status == ClaimStatus.Downgraded))
        {
            return new DeliveryDecision(DeliveryDecisionKind.Review, "unverified claim present");
        }

        var gaps = finding.Claims.Count(c => c.Status == ClaimStatus.Supported && c.Kind == ClaimKind.MissingRequirement);
        if (gaps == 1)
        {
            if (finding.Claims.Count != 1)
            {
                return new DeliveryDecision(DeliveryDecisionKind.Review, "ambiguous: gap mixed with other claims");
            }

            return new DeliveryDecision(DeliveryDecisionKind.Eligible, "clear validated gap on current basis");
        }

        if (gaps > 1)
        {
            return new DeliveryDecision(DeliveryDecisionKind.Review, "ambiguous: multiple missing requirements");
        }

        return finding.Claims.Any(c => c.Status == ClaimStatus.Supported && c.Kind == ClaimKind.Baseline)
            ? new DeliveryDecision(DeliveryDecisionKind.CompleteBaseline, "evidence-linked baseline")
            : new DeliveryDecision(DeliveryDecisionKind.Blocked, "unsupported finding state: no supported claims");
    }
}

public enum DeliveryOutcome
{
    Delivered,
    Replayed,
    Conflict,
    ScenarioTimeMismatch,
    Review,
    Blocked,
    CompleteBaseline,
}

public sealed record DeliveryReceipt(
    string EventId,
    string RequestId,
    string CaseId,
    string ReportId,
    string BasisId,
    int BasisVersion,
    string TemplateVersion,
    string Body);

public sealed record DeliveryResult(DeliveryOutcome Outcome, int HttpStatus, string Reason, DeliveryReceipt? Receipt);

/// <summary>
/// Internal mock inbox. It has no network dependency, so no path contacts an external party. Assessments persist
/// receipts before recording them here; this in-memory state supports replay checks within the current process.
/// </summary>
public sealed class MockRequestInbox
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (string Fingerprint, DeliveryReceipt Receipt)> _byEvent = new(StringComparer.Ordinal);
    private readonly Dictionary<(string CaseId, string ReportId), string> _reportEvent = new();
    private readonly List<DeliveryReceipt> _requests = [];
    private readonly List<string> _audit = [];

    /// <summary>Fault-injection switch: when true, the write boundary throws before any state changes.</summary>
    public bool InjectWriteFailure { get; set; }

    public IReadOnlyList<DeliveryReceipt> Requests
    {
        get { lock (_gate) return _requests.ToArray(); }
    }

    public IReadOnlyList<string> Audit
    {
        get { lock (_gate) return _audit.ToArray(); }
    }

    public DeliveryResult Submit(DeliveryEnvelope envelope, DeliveryFinding finding, string expectedScenarioTime)
        => Evaluate(envelope, finding, expectedScenarioTime, record: true);

    public DeliveryResult Prepare(
        DeliveryEnvelope envelope,
        DeliveryFinding finding,
        string expectedScenarioTime)
        => Evaluate(envelope, finding, expectedScenarioTime, record: false);

    public void RecordPrepared(DeliveryEnvelope envelope, DeliveryReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(receipt);
        lock (_gate)
        {
            var fingerprint = Fingerprint(envelope);
            if (_byEvent.TryGetValue(envelope.EventId, out var existing))
            {
                if (existing.Fingerprint != fingerprint)
                    throw new InvalidOperationException("Prepared delivery conflicts with an existing event.");
                return;
            }

            if (_reportEvent.ContainsKey((envelope.CaseId, envelope.ReportId)))
                throw new InvalidOperationException("Prepared delivery conflicts with an existing report.");

            RecordLocked(envelope, receipt, fingerprint);
        }
    }

    private DeliveryResult Evaluate(
        DeliveryEnvelope envelope,
        DeliveryFinding finding,
        string expectedScenarioTime,
        bool record)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(finding);

        if (!string.Equals(envelope.ScenarioTime, expectedScenarioTime, StringComparison.Ordinal))
        {
            return new DeliveryResult(DeliveryOutcome.ScenarioTimeMismatch, 400, "scenario time mismatch", null);
        }

        var fingerprint = Fingerprint(envelope);
        lock (_gate)
        {
            if (_byEvent.TryGetValue(envelope.EventId, out var existing))
            {
                return existing.Fingerprint == fingerprint
                    ? new DeliveryResult(DeliveryOutcome.Replayed, 200, "exact replay", existing.Receipt)
                    : new DeliveryResult(DeliveryOutcome.Conflict, 409, "event id reused with a changed envelope", null);
            }

            if (_reportEvent.ContainsKey((envelope.CaseId, envelope.ReportId)))
            {
                return new DeliveryResult(DeliveryOutcome.Conflict, 409, "report id already scoped to another event", null);
            }

            var decision = MockDeliveryPolicy.Evaluate(finding, envelope);
            if (decision.Kind != DeliveryDecisionKind.Eligible)
            {
                var outcome = decision.Kind switch
                {
                    DeliveryDecisionKind.CompleteBaseline => DeliveryOutcome.CompleteBaseline,
                    DeliveryDecisionKind.Blocked => DeliveryOutcome.Blocked,
                    _ => DeliveryOutcome.Review,
                };
                return new DeliveryResult(outcome, 200, decision.Reason, null);
            }

            var requestId = RequestIdFor(envelope);
            var receipt = new DeliveryReceipt(
                envelope.EventId,
                requestId,
                envelope.CaseId,
                envelope.ReportId,
                envelope.BasisId,
                envelope.BasisVersion,
                MockRequestTemplate.Version,
                MockRequestTemplate.Render(envelope, requestId));

            if (InjectWriteFailure)
            {
                throw new InvalidOperationException("injected write failure");
            }

            if (record) RecordLocked(envelope, receipt, fingerprint);
            return new DeliveryResult(DeliveryOutcome.Delivered, 201, "delivered", receipt);
        }
    }

    private void RecordLocked(DeliveryEnvelope envelope, DeliveryReceipt receipt, string fingerprint)
    {
        _byEvent[envelope.EventId] = (fingerprint, receipt);
        _reportEvent[(envelope.CaseId, envelope.ReportId)] = envelope.EventId;
        _requests.Add(receipt);
        _audit.Add($"delivered {receipt.RequestId} for event {envelope.EventId} on basis {envelope.BasisId} v{envelope.BasisVersion}");
    }

    private static string RequestIdFor(DeliveryEnvelope envelope) =>
        "REQ-" + Digest(envelope.CaseId, envelope.ReportId)[..16];

    private static string Fingerprint(DeliveryEnvelope e) =>
        Digest(
            e.EventId,
            e.CaseId,
            e.ReportId,
            e.BasisId,
            e.BasisVersion.ToString(CultureInfo.InvariantCulture),
            e.ScenarioTime,
            e.Component,
            e.SerialNumber,
            e.MissingRequirement);

    // Each field is prefixed with its length in UTF-16 code units, then hashed as raw code units. UTF-8 would replace
    // unpaired surrogates with U+FFFD and let distinct envelopes collide.
    private static string Digest(params string[] fields)
    {
        using var stream = new MemoryStream();
        foreach (var field in fields)
        {
            stream.Write(Encoding.ASCII.GetBytes(field.Length.ToString(CultureInfo.InvariantCulture) + ":"));
            stream.Write(MemoryMarshal.AsBytes(field.AsSpan()));
        }
        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }
}

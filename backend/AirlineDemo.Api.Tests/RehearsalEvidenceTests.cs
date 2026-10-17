using System.Text.RegularExpressions;

namespace AirlineDemo.Api.Tests;

public sealed class RehearsalEvidenceTests
{
    private static readonly string Root = FindRepoRoot();
    private static readonly string Checklist = File.ReadAllText(Path.Combine(Root, "docs", "aircraft-return-readiness", "rehearsal-checklist.md"));
    private static readonly string Evidence = File.ReadAllText(Path.Combine(Root, "docs", "aircraft-return-readiness", "rehearsal-evidence.md"));

    private static readonly string[] RequiredChecklistSteps =
    [
        "**Clean revision:**",
        "**Seed synthetic case:**",
        "**Real 30-second trace capture:**",
        "**Outcome within 20 seconds:**",
        "**Live segment within 90 seconds or fallback:**",
        "**Consequential simulated decision:**",
        "**Response-driven reassessment:**",
        "**Evidence and result review:**",
    ];

    private static readonly string[] RequiredEvidenceFields =
    [
        "Repository revision",
        "Application version and immutable image digest",
        "Fixture ID",
        "Fixture hash",
        "Suite version",
        "Suite hash",
        "Model manifest version and approval reference",
        "Deployment configuration (environment name and non-secret configuration reference)",
        "Build run ID",
        "Release run ID",
        "Run IDs (demo and evaluation arms, if evaluation was performed)",
        "Actual timings (outcome, live segment, and trace capture)",
        "Outcome timing (trigger to first visible outcome; measured value and unit)",
        "Live-segment timing (first action through decision and reassessment; measured value and unit)",
        "Trace-capture timing (recording duration; measured value and unit)",
        "Observed results (outcome, smoke checks, failures, and evaluation outputs)",
        "Observed outcome and readiness state",
        "Smoke-check results",
        "Failures, pending/error states, and limitations",
        "Evaluation outputs",
        "Real investigation trace reference (restricted evidence location and run correlation)",
        "Trace resource attributes (`service.name`, `service.version`, `deployment.environment`)",
        "Simulated decision (reviewer role, consequential action, reason)",
        "Decision business-audit reference",
        "Synthetic response reference",
        "Response-driven reassessment reference and observed result",
        "Reviewer verification of trace and audit evidence (name and date)",
        "Fallback used and reason",
        "Fallback fixture ID",
        "Fallback application version",
        "Fallback evidence location",
        "Fallback limitations",
        "Reviewer verification that the fallback is reproducible (name and date)",
        "Product owner",
        "Rehearsal date",
        "Approved restricted evidence location",
        "Evidence-record commit",
    ];

    [Fact]
    public void Checklist_covers_each_required_rehearsal_step()
    {
        Assert.Contains("Run this once", Checklist);
        Assert.Contains("NOT PERFORMED", Evidence);
        foreach (var step in RequiredChecklistSteps)
        {
            Assert.Contains($"- [ ] {step}", Checklist);
            var withoutStep = Checklist.Replace($"- [ ] {step}", "", StringComparison.Ordinal);
            Assert.False(HasAllChecklistSteps(withoutStep), $"Checklist must reject omission of {step}");
        }

        Assert.Contains("20 seconds or less", Checklist);
        Assert.Contains("90 seconds or less", Checklist);
        Assert.Contains("30-second recording", Checklist);
        Assert.Contains("synthetic mock-inbox response", Checklist);
    }

    [Fact]
    public void Evidence_record_contains_every_required_field()
    {
        foreach (var field in RequiredEvidenceFields)
        {
            var line = Evidence.Split('\n').SingleOrDefault(line => line.StartsWith($"- {field}:", StringComparison.Ordinal));
            Assert.NotNull(line);
            var withoutField = Evidence.Replace(line, "", StringComparison.Ordinal);
            Assert.False(HasAllEvidenceFields(withoutField), $"Evidence record must reject omission of {field}");
        }

        Assert.Contains("**Rehearsal status: NOT PERFORMED.**", Evidence);
        Assert.All(RequiredEvidenceFields, field => Assert.Empty(FieldValue(Evidence, field)));
    }

    [Fact]
    public void Fallback_identifies_fixture_and_application_version()
    {
        var fallback = Section(Evidence, "Recorded fallback");
        Assert.False(HasFallbackIdentity(fallback));
        Assert.True(HasFallbackIdentity("- Fallback fixture ID: synthetic-fixture-1\n- Fallback application version: 1.0.0\n"));
        Assert.False(HasFallbackIdentity("- Fallback fixture ID: synthetic-fixture-1\n- Fallback application version:\n"));
        Assert.False(HasFallbackIdentity("- Fallback fixture ID:\n- Fallback application version: 1.0.0\n"));
        Assert.False(HasFallbackIdentity("- Fallback fixture ID:\n- Fallback application version:\n"));
        Assert.False(HasFallbackIdentity("- Fallback fixture ID: not recorded\n- Fallback application version: 1.0.0\n"));
        Assert.False(HasFallbackIdentity("- Fallback fixture ID: synthetic-fixture-1\n- Fallback application version: unknown\n"));
    }

    private static bool HasAllChecklistSteps(string content) =>
        RequiredChecklistSteps.All(step => content.Contains($"- [ ] {step}", StringComparison.Ordinal));

    private static bool HasAllEvidenceFields(string content) =>
        RequiredEvidenceFields.All(field => Regex.IsMatch(content, $@"(?m)^- {Regex.Escape(field)}:"));

    private static bool HasFallbackIdentity(string content)
    {
        var fixtureId = FieldValue(content, "Fallback fixture ID");
        var applicationVersion = FieldValue(content, "Fallback application version");
        return IsRecordedValue(fixtureId) && IsRecordedValue(applicationVersion);
    }

    private static bool IsRecordedValue(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !new[] { "not recorded", "not available", "unknown", "none", "pending", "n/a" }
            .Contains(value, StringComparer.OrdinalIgnoreCase);

    private static string FieldValue(string content, string field)
    {
        var match = Regex.Match(content, $@"(?m)^- {Regex.Escape(field)}:[ \t]*(.*)$");
        return match.Success ? match.Groups[1].Value.Trim() : "";
    }

    private static string Section(string content, string heading)
    {
        var start = content.IndexOf($"## {heading}", StringComparison.Ordinal);
        if (start < 0)
        {
            return "";
        }

        var end = content.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        return end < 0 ? content[start..] : content[start..end];
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, ".github", "workflows", "build.yml")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}

using System.Text.RegularExpressions;

namespace AirlineDemo.Api.Tests;

public sealed class ReadinessDocumentationTests
{
    private static readonly string Root = FindRepoRoot();
    private static readonly string Readme = File.ReadAllText(Path.Combine(Root, "docs", "aircraft-return-readiness", "README.md"));

    [Fact]
    public void Readme_has_every_required_readiness_section()
    {
        foreach (var heading in new[]
        {
            "Purpose and business story", "AI, human in the loop and autonomy", "Architecture", "Prerequisites",
            "Safe configuration", "Setup and deployment", "Seed, run and reset", "Evaluation and results",
            "Cost disclosure", "Removal and teardown", "Limitations",
        })
        {
            Assert.Contains($"## {heading}", Readme);
        }
    }

    [Fact]
    public void AI_human_autonomy_section_covers_every_capability_and_level()
    {
        var section = Section(Readme, "AI, human in the loop and autonomy");
        Assert.Contains("bounded, read-only tools", section);
        Assert.Contains("typed claims", section);
        Assert.Contains("checks each claim against document pages", section);
        Assert.Contains("sends nothing and decides nothing", section);
        Assert.Contains("Deterministic rules handle policy, requests to the mock inbox, reserves, obligation status, and notice drafts", section);
        Assert.Contains("Ambiguous or conflicting evidence, technical acceptance, finance review, and legal review require a person", section);
        Assert.Contains("simulated; decisions are recorded as human actor events", section);
        Assert.Contains("| Records investigation | L4, autonomous | L3: acts only on routine evidence requests within policy to the mock inbox; a person decides ambiguous cases and technical acceptance |", section);
        Assert.Contains("| Utilisation and reserves | L3, acts (invoices) with finance review | L2: drafts a reserve calculation; a simulated finance review decides; no invoice |", section);
        Assert.Contains("| Contract compliance | L3, acts (notices) with legal sign-off | L2: drafts an obligation status and notice; a simulated legal review decides; no notice sent |", section);
        Assert.Contains("| Transition planning | L2, recommend | design-only: not implemented in this demo |", section);
        Assert.Contains("| Credit and sanctions monitoring | L2, recommend | design-only: not implemented in this demo |", section);
        Assert.Contains("| Portfolio supervisor | not in the case-study table | design-only: not implemented in this demo |", section);
        Assert.Contains("no invoices, notices, sanctions decisions, or airworthiness decisions", section);
        Assert.Contains("uses synthetic data only", section);
        Assert.Contains("four-arm evaluation", section);
        Assert.Contains("no scored result exists until the model manifest is approved", section);
        Assert.Contains("[four-arm evaluation](#evaluation-and-results)", section);
    }

    [Fact]
    public void Setup_documents_the_full_reproducible_lifecycle()
    {
        var section = Section(Readme, "Setup and deployment");
        foreach (var required in new[]
        {
            "repository revision", "protected state bootstrap", "terraform plan", "terraform apply",
            "repeated Terraform plan", "deployment", "smoke-check", "Seed, run and reset",
            "Removal and teardown", "Entra", "private connectivity", "Terraform only",
            "No manual portal resource creation",
        })
        {
            Assert.Contains(required, section, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("terraform -chdir=infra/terraform init -backend-config=", section);
        Assert.Contains("terraform -chdir=infra/terraform plan -var-file=", section);
        Assert.Contains("-out=/absolute/path/reviewed.tfplan", section);
        Assert.Contains("terraform -chdir=infra/terraform apply /absolute/path/reviewed.tfplan", section);
        Assert.Contains("-out=/absolute/path/repeated.tfplan", section);
    }

    [Fact]
    public void Unapproved_costs_are_stubbed_and_not_claimed_as_estimates()
    {
        var section = Section(Readme, "Cost disclosure");
        foreach (var item in new[]
        {
            "Azure compute", "Azure SQL", "Azure Blob storage", "Azure Container Registry",
            "Model input tokens", "Model output tokens",
        })
        {
            Assert.Contains($"| {item} | STUB |", section);
        }

        Assert.Contains("No numeric estimate or spending cap is approved", section);
        Assert.DoesNotMatch(new Regex(@"\b\d+(?:[.,]\d+)?\b"), section);
    }

    [Fact]
    public void Readiness_evidence_links_and_template_fields_are_complete()
    {
        Assert.Contains("[case design](../prd/aircraft-return-readiness.md)", Readme);
        Assert.Contains("[scored-evaluation format](../../backend/AirlineDemo.Api.Tests/Fixtures/Evaluation/README.md)", Readme);
        Assert.Contains("[rehearsal evidence](rehearsal-evidence.md)", Readme);

        var evidence = File.ReadAllText(Path.Combine(Root, "docs", "aircraft-return-readiness", "rehearsal-evidence.md"));
        foreach (var field in new[]
        {
            "Repository revision", "Application version", "Fixture hash", "Suite hash",
            "Model manifest version", "Run IDs", "Actual timings", "Real investigation trace",
            "Simulated decision", "Fallback", "Observed results",
        })
        {
            Assert.Contains($"- {field}", evidence);
        }

        foreach (var content in new[] { Readme, evidence })
        {
            Assert.Contains("synthetic", content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("simulated", content, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string Section(string text, string heading)
    {
        var start = text.IndexOf($"## {heading}", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing section: {heading}");
        var end = text.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        return end < 0 ? text[start..] : text[start..end];
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

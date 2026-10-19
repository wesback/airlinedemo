using System.Text.RegularExpressions;

namespace AirlineDemo.Api.Tests;

public sealed class ReadinessDocumentationTests
{
    private static readonly string Root = FindRepoRoot();
    private static readonly string Readme = File.ReadAllText(Path.Combine(Root, "docs", "aircraft-return-readiness", "README.md"));
    private static readonly string CostModel = File.ReadAllText(Path.Combine(Root, "docs", "aircraft-return-readiness", "cost-model.md"));

    private static readonly string[] CostComponents =
    {
        "Azure compute", "Azure SQL", "Azure Blob Storage", "Azure Container Registry",
        "Model input tokens", "Model output tokens",
    };

    private static readonly string[] LifecycleStates = { "active", "idle", "residual" };

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
    public void Cost_model_lists_every_component_and_lifecycle_state()
    {
        var rows = CostRows(CostModel);
        Assert.Equal(18, rows.Length);
        foreach (var component in CostComponents)
        foreach (var lifecycle in LifecycleStates)
        {
            Assert.Single(rows.Where(row => row[0] == component && row[1] == lifecycle));
        }
    }

    [Fact]
    public void Cost_model_records_sources_assumptions_contingencies_and_stub_values()
    {
        var rows = CostRows(CostModel);
        foreach (var row in rows)
        {
            Assert.All(row.Skip(3).Take(5), value => Assert.False(string.IsNullOrWhiteSpace(value)));
            Assert.Equal("unapproved", row[7]);
            Assert.Equal("STUB", row[2]);
        }

        var ceiling = Regex.Match(CostModel, @"(?m)^Spending ceiling:\s*(.+)$");
        Assert.True(ceiling.Success);
        Assert.Equal("not set", ceiling.Groups[1].Value.Trim());
        Assert.True(UnapprovedCostsAreStubbedAndCeilingIsUnset(CostModel));

        var numericUnapprovedPrice = CostModel.Replace(
            "| Azure compute | active | STUB |",
            "| Azure compute | active | 1.00 |",
            StringComparison.Ordinal);
        Assert.False(UnapprovedCostsAreStubbedAndCeilingIsUnset(numericUnapprovedPrice));

        var numericSpendingCeiling = CostModel.Replace(
            "Spending ceiling: not set",
            "Spending ceiling: EUR100 (scenario assumption)",
            StringComparison.Ordinal);
        Assert.False(UnapprovedCostsAreStubbedAndCeilingIsUnset(numericSpendingCeiling));
    }

    [Fact]
    public void Cost_model_requires_separate_cost_and_execution_approval()
    {
        var section = Section(CostModel, "Approval record");
        var fields = ApprovalFields(CostModel);
        var expectedFields = new[]
        {
            "approverName", "approvalDate", "authorizedCostScope", "authorizedExecutionScope", "authorizationStatus",
        };
        Assert.Equal(expectedFields.Order(), fields.Keys.Order());
        Assert.Equal("not authorized", fields["authorizationStatus"]);
        Assert.True(IsValidApprovalRecord(CostModel));
        Assert.Contains("Cost approval and execution approval are separate decisions", section);
        Assert.Contains("Both the cost scope and the execution scope require explicit approval by the owner of the target environment", section);

        var authorized = SetApprovalField(CostModel, "authorizationStatus", "authorized");
        Assert.False(IsValidApprovalRecord(authorized));
        foreach (var field in expectedFields.Where(field => field != "authorizationStatus"))
        {
            Assert.False(IsValidApprovalRecord(SetApprovalField(authorized, field, "")));
            Assert.False(IsValidApprovalRecord(RemoveApprovalField(authorized, field)));
        }

        Assert.True(IsValidApprovalRecord(SetApprovalField(authorized, "approverName", "Owner")
            .Replace("approvalDate: \"\"", "approvalDate: \"2026-10-17\"", StringComparison.Ordinal)
            .Replace("authorizedCostScope: \"\"", "authorizedCostScope: \"approved cost\"", StringComparison.Ordinal)
            .Replace("authorizedExecutionScope: \"\"", "authorizedExecutionScope: \"approved execution\"", StringComparison.Ordinal)));
    }

    [Fact]
    public void Value_and_break_even_figures_are_labelled_and_never_invented()
    {
        var section = Section(CostModel, "Value and break-even");
        Assert.Contains("PRD DD-043", section);
        Assert.Contains("AC-025", section);
        Assert.Contains("EUR250,000 per idle aircraft-month (case-study stipulation)", section);
        Assert.Contains("30.4 days per month (case-study stipulation)", section);
        Assert.Contains("approximately EUR8,200 per recovered idle-aircraft-day (derived)", section);
        var platformCostDerivation = section.Split('\n')
            .Single(line => line.StartsWith("Platform cost per transition prepared is derived", StringComparison.Ordinal));
        Assert.Equal(
            "Platform cost per transition prepared is derived from the rows above. For each component/lifecycle row, multiply its approved unit price by measured units for that row; sum the resulting lifecycle costs and divide by the number of transitions prepared in the same measured period. It remains `STUB` while any component price is unapproved or usage and transition counts are unmeasured.",
            platformCostDerivation);
        Assert.Contains("break-even days are exactly not calculated", section);
        Assert.Contains("No value below is validated customer savings.", section);

        var figurePattern = new Regex(@"(?<![\w-])(?:EUR\s*)?\d+(?:,\d{3})*(?:\.\d+)?(?:\s*EUR)?(?![\w-])");
        var labels = new[] { "case-study stipulation", "external evidence", "scenario assumption", "architectural decision", "synthetic demonstration result", "unvalidated target", "derived" };
        foreach (var line in CostModel.Split('\n').Where(line => figurePattern.IsMatch(line)))
        {
            var figures = figurePattern.Matches(line).Cast<Match>().ToArray();
            for (var index = 0; index < figures.Length; index++)
            {
                var end = index + 1 < figures.Length ? figures[index + 1].Index : line.Length;
                var labelText = line[(figures[index].Index + figures[index].Length)..end];
                Assert.Equal(1, labels.Sum(label => Regex.Matches(labelText, Regex.Escape(label)).Count));
            }
        }
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

    private static string[][] CostRows(string content)
    {
        return content.Split('\n')
            .Where(line => line.StartsWith('|'))
            .Select(line => line.Split('|').Skip(1).Take(8).Select(cell => cell.Trim()).ToArray())
            .Where(row => row.Length == 8 && CostComponents.Contains(row[0]) && LifecycleStates.Contains(row[1]))
            .ToArray();
    }

    private static bool UnapprovedCostsAreStubbedAndCeilingIsUnset(string content)
    {
        var ceiling = Regex.Match(content, @"(?m)^Spending ceiling:\s*(.+)$");
        return CostRows(content).All(row => row[7] != "unapproved" || row[2] == "STUB") &&
            ceiling.Success &&
            ceiling.Groups[1].Value.Trim() == "not set";
    }

    private static Dictionary<string, string> ApprovalFields(string content)
    {
        return Regex.Matches(Section(content, "Approval record"), @"(?m)^([A-Za-z]+):\s*""([^""]*)""$")
            .ToDictionary(match => match.Groups[1].Value, match => match.Groups[2].Value);
    }

    private static bool IsValidApprovalRecord(string content)
    {
        var fields = ApprovalFields(content);
        var required = new[] { "approverName", "approvalDate", "authorizedCostScope", "authorizedExecutionScope", "authorizationStatus" };
        if (fields.Count != required.Length || required.Any(field => !fields.ContainsKey(field)))
        {
            return false;
        }

        var status = fields["authorizationStatus"];
        if (status == "not authorized")
        {
            return required.Where(field => field != "authorizationStatus").All(field => fields[field].Length == 0);
        }

        return status == "authorized" &&
            required.Where(field => field != "authorizationStatus").All(field => !string.IsNullOrWhiteSpace(fields[field]));
    }

    private static string SetApprovalField(string content, string field, string value)
    {
        return Regex.Replace(content, $@"(?m)^{Regex.Escape(field)}:\s*""[^""]*""$", $"{field}: \"{value}\"");
    }

    private static string RemoveApprovalField(string content, string field)
    {
        return Regex.Replace(content, $@"(?m)^{Regex.Escape(field)}:\s*""[^""]*""\r?\n", "");
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

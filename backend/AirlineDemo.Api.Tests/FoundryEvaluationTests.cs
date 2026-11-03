using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AirlineDemo.Api.Investigation;

namespace AirlineDemo.Api.Tests;

public sealed class FoundryEvaluationTests
{
    private const string FrozenRowsHash = "0b5896cbe978d6cbcff44864a06969b1a718cd1ad9444b5eef7543dfbcb79209";

    private static string Fixture(string file, [CallerFilePath] string source = "") =>
        Path.Combine(Path.GetDirectoryName(source)!, "Fixtures", "Foundry", "v1", file);

    [Fact]
    public void Frozen_input_matrix_matches_exact_contract()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Fixture("manifest.json")));
        Assert.Equal(1, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("1.0.0", manifest.RootElement.GetProperty("fixtureVersion").GetString());
        Assert.Equal("fixture-pr-merge", manifest.RootElement.GetProperty("approvalMechanism").GetString());
        Assert.Equal("inputs.jsonl", manifest.RootElement.GetProperty("rowsFile").GetString());
        Assert.Equal(12, manifest.RootElement.GetProperty("rowCount").GetInt32());
        Assert.Equal(FrozenRowsHash, manifest.RootElement.GetProperty("rowsSha256").GetString());

        var lines = File.ReadAllLines(Fixture("inputs.jsonl"), Encoding.UTF8);
        Assert.Equal(12, lines.Length);
        Assert.Equal(FrozenRowsHash,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Fixture("inputs.jsonl")))).ToLowerInvariant());

        var expected = new[] { "technical-reviewer", "finance-reviewer", "legal-reviewer" }
            .SelectMany(role => new[] { "cp-case-a", "cp-case-b", "cp-case-c" }
                .Select(caseId => ("copilot", role, caseId)))
            .Concat([
                ("copilot", "presenter", "cp-case-a"),
                ("copilot", "executive", "cp-case-b"),
                ("investigator", "technical-reviewer", "cp-case-c"),
            ])
            .ToArray();

        for (var index = 0; index < lines.Length; index++)
        {
            using var row = JsonDocument.Parse(lines[index]);
            var value = row.RootElement;
            Assert.Equal("FOUNDRY-V1-" + (index + 1).ToString("00", CultureInfo.InvariantCulture),
                value.GetProperty("rowId").GetString());
            Assert.Equal(expected[index],
                (value.GetProperty("workload").GetString(), value.GetProperty("role").GetString(),
                    value.GetProperty("caseId").GetString()));
            Assert.Equal(new[] { "caseId", "input", "inputSha256", "role", "rowId", "workload" },
                value.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
            var input = value.GetProperty("input");
            Assert.Equal(value.GetProperty("inputSha256").GetString(),
                SyntheticContentRegistry.Sha256Hex(input.GetRawText()));
            if (index < 11)
            {
                Assert.Equal(new[] { "question" }, input.EnumerateObject().Select(property => property.Name).ToArray());
            }
        }
    }

    [Fact]
    public void Investigator_input_is_authorized_by_the_committed_registry()
    {
        using var row = InvestigatorRow();
        var input = row.RootElement.GetProperty("input");
        Assert.Equal("investigate-return-readiness", input.GetProperty("task").GetString());
        Assert.Equal("v1", input.GetProperty("evidenceVersion").GetString());
        var evidence = ReadCommittedEvidence(input);
        var trace = ModelCallGate.Authorize(Manifest(),
            new ModelRequest(input.GetProperty("evidenceVersion").GetString(), "swedencentral", evidence),
            SyntheticContentRegistry.FromCommittedFixtures());
        Assert.Equal(input.GetProperty("evidence").EnumerateArray()
            .Select(item => item.GetProperty("sha256").GetString()).ToArray(), trace.EvidenceSha256);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Changed_investigator_evidence_is_refused_by_the_production_gate(int index)
    {
        using var row = InvestigatorRow();
        var input = row.RootElement.GetProperty("input");
        var evidence = ReadCommittedEvidence(input);
        evidence[index] += " changed";
        var exception = Assert.Throws<ModelGateException>(() => ModelCallGate.Authorize(Manifest(),
            new ModelRequest(input.GetProperty("evidenceVersion").GetString(), "swedencentral", evidence),
            SyntheticContentRegistry.FromCommittedFixtures()));
        Assert.Equal("model request refused: evidence SHA-256 is not registered as synthetic content", exception.Message);
    }

    private static JsonDocument InvestigatorRow() =>
        JsonDocument.Parse(File.ReadLines(Fixture("inputs.jsonl")).Last());

    private static string[] ReadCommittedEvidence(JsonElement input)
    {
        var assembly = typeof(SyntheticContentRegistry).Assembly;
        return input.GetProperty("evidence").EnumerateArray().Select(item =>
        {
            var name = "AirlineDemo.Api.Investigation.SyntheticFixtures." + item.GetProperty("resource").GetString();
            using var stream = assembly.GetManifestResourceStream(name);
            Assert.NotNull(stream);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var content = reader.ReadToEnd().TrimEnd('\r', '\n');
            Assert.Equal(item.GetProperty("sha256").GetString(), SyntheticContentRegistry.Sha256Hex(content));
            return content;
        }).ToArray();
    }

    private static ApprovedModelManifest Manifest() => new(
        "1.0.0", true, ApprovedModelManifest.LiveArm, "fixture-model", "gpt-4.1",
        "2025-04-14", "v1", "swedencentral", ApprovedModelManifest.EnabledContentFilter);
}

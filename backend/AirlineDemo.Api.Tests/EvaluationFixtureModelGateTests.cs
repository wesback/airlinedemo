using System.Runtime.CompilerServices; using System.Text.Json; using System.Text.Json.Nodes;
using AirlineDemo.Api.Evaluation;
namespace AirlineDemo.Api.Tests;
public sealed class EvaluationFixtureModelGateTests
{
    private static string Dir([CallerFilePath] string source = "") => Path.Combine(Path.GetDirectoryName(source)!, "Fixtures", "Evaluation", "v2");
    private static string Copy() { var d = Path.Combine(Path.GetTempPath(), "fixture-gate-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d);
        foreach (var f in new[] { "manifest.json", "cases.jsonl" }) File.Copy(Path.Combine(Dir(), f), Path.Combine(d, f)); return d; }
    [Fact]
    public void AC016_AuthorizesOnlyVerifiedV2FixturePages()
    {
        var dir = Copy(); var calls = 0;
        try
        {
            var gate = new EvaluationFixtureModelGate(Dir()); using var row = JsonDocument.Parse(File.ReadLines(Path.Combine(Dir(), "cases.jsonl")).First());
            var item = row.RootElement; var id = item.GetProperty("caseId").GetString()!; var page = item.GetProperty("inputs").GetProperty("documents")[0].GetProperty("pageText").GetString()!;
            gate.AuthorizePageText(id, page); calls++;
            Assert.Throws<EvaluationFixtureAuthorizationException>(() => { gate.AuthorizePageText(id, page + " changed"); calls++; });
            var casesPath = Path.Combine(dir, "cases.jsonl"); var cases = File.ReadAllText(casesPath);
            File.WriteAllText(casesPath, cases.Replace("\"inputVersion\":2", "\"inputVersion\":2 ", StringComparison.Ordinal));
            Assert.Throws<EvaluationFixtureAuthorizationException>(() => new EvaluationFixtureModelGate(dir));
            File.WriteAllText(casesPath, cases);
            var path = Path.Combine(dir, "manifest.json"); var manifest = File.ReadAllText(path); var missing = JsonNode.Parse(manifest)!.AsObject(); missing.Remove("casesSha256");
            File.WriteAllText(path, missing.ToJsonString()); Assert.Throws<EvaluationFixtureAuthorizationException>(() => new EvaluationFixtureModelGate(dir));
            var mismatch = JsonNode.Parse(manifest)!.AsObject(); mismatch["casesSha256"] = new string('0', 64);
            File.WriteAllText(path, mismatch.ToJsonString()); Assert.Throws<EvaluationFixtureAuthorizationException>(() => new EvaluationFixtureModelGate(dir));
            File.WriteAllText(path, manifest.Replace("\"fixtureVersion\": 2", "\"fixtureVersion\": 3", StringComparison.Ordinal));
            Assert.Throws<EvaluationFixtureAuthorizationException>(() => new EvaluationFixtureModelGate(dir));
            Assert.Equal(1, calls);
        }
        finally { Directory.Delete(dir, true); }
    }
}

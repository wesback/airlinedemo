using System.Security.Cryptography; using System.Text; using System.Text.Json;
namespace AirlineDemo.Api.Evaluation;
public sealed record EvaluationFixtureDocument(string DocumentId, int PageNumber, string PageText, string ParseStatus); public sealed record VerifiedEvaluationFixtureCase(string CaseId, int InputVersion, string InputsJson, string InputsSha256, IReadOnlyList<EvaluationFixtureDocument> Documents);
public sealed class EvaluationFixtureAuthorizationException(string message) : Exception(message);
/// <summary>Evaluation-only authorization for exact committed v2 fixture data; production calls remain on ModelCallGate.</summary>
public sealed class EvaluationFixtureModelGate
{
    public const string FrozenCasesSha256 = "7318583ee0bdc540fdeabd85833d288843c2a1bc429b1f85c9709eac8443a049";
    private readonly Dictionary<string, VerifiedEvaluationFixtureCase> _cases;
    public EvaluationFixtureModelGate(string fixtureDirectory)
    {
        try
        {
            using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixtureDirectory, "manifest.json")));
            var root = manifest.RootElement; var bytes = File.ReadAllBytes(Path.Combine(fixtureDirectory, "cases.jsonl"));
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (root.GetProperty("fixtureVersion").GetInt32() != 2 || root.GetProperty("suite").GetString() != "evaluation-v2" ||
                root.GetProperty("caseCount").GetInt32() != 40 || root.GetProperty("casesSha256").GetString() != hash || hash != FrozenCasesSha256)
                throw new EvaluationFixtureAuthorizationException("evaluation fixture manifest or hash is not the committed v2 corpus");
            _cases = new(StringComparer.Ordinal);
            foreach (var line in new UTF8Encoding(false, true).GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                using var row = JsonDocument.Parse(line); var item = row.RootElement; var id = item.GetProperty("caseId").GetString()!;
                var inputs = item.GetProperty("inputs"); var raw = inputs.GetRawText();
                var docs = inputs.GetProperty("documents").EnumerateArray().Select(d => new EvaluationFixtureDocument(
                    d.GetProperty("documentId").GetString()!, d.GetProperty("pageNumber").GetInt32(),
                    d.GetProperty("pageText").GetString()!, d.GetProperty("parseStatus").GetString()!)).ToArray();
                if (item.GetProperty("inputVersion").GetInt32() != 2 || !_cases.TryAdd(id, new(id, 2, raw,
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant(), docs)))
                    throw new EvaluationFixtureAuthorizationException("evaluation fixture has an invalid or duplicate case");
            }
            if (_cases.Count != 40) throw new EvaluationFixtureAuthorizationException("evaluation fixture must contain 40 cases");
        }
        catch (EvaluationFixtureAuthorizationException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or DecoderFallbackException or KeyNotFoundException or InvalidOperationException)
        { throw new EvaluationFixtureAuthorizationException("evaluation fixture could not be verified: " + ex.Message); }
    }
    public VerifiedEvaluationFixtureCase AuthorizeInputs(string caseId, int version, string inputsJson, string inputsSha256)
    {
        if (!_cases.TryGetValue(caseId, out var item) || version != item.InputVersion ||
            !string.Equals(inputsJson, item.InputsJson, StringComparison.Ordinal) || !string.Equals(inputsSha256, item.InputsSha256, StringComparison.Ordinal))
            throw new EvaluationFixtureAuthorizationException("evaluation inputs do not match the verified case bytes");
        return item;
    }
    public void AuthorizePageText(string caseId, string pageText)
    { if (!_cases.TryGetValue(caseId, out var item) || !item.Documents.Any(d => string.Equals(d.PageText, pageText, StringComparison.Ordinal))) throw new EvaluationFixtureAuthorizationException("page text is not an exact page in the verified v2 case"); }
    public static EvaluationFixtureModelGate FromCommittedFixtures()
    {
        var roots = new List<DirectoryInfo>(); for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent) roots.Add(dir);
        var path = roots.SelectMany(d => new[] { Path.Combine(d.FullName, "backend", "AirlineDemo.Api.Tests", "Fixtures", "Evaluation", "v2"),
            Path.Combine(d.FullName, "Fixtures", "Evaluation", "v2") }).FirstOrDefault(Directory.Exists);
        return path is null ? throw new EvaluationFixtureAuthorizationException("committed evaluation v2 fixtures were not found") : new(path);
    }
}

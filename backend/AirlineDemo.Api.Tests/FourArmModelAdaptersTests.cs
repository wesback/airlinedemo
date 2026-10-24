using System.Runtime.CompilerServices; using System.Security.Cryptography;
using System.Text; using System.Text.Json;
using AirlineDemo.Api.Evaluation; using AirlineDemo.Api.Investigation;
using Microsoft.Extensions.AI;
namespace AirlineDemo.Api.Tests;
public sealed class FourArmModelAdaptersTests
{
    private static string Fixture([CallerFilePath] string source = "") => Path.Combine(Path.GetDirectoryName(source)!, "Fixtures", "Evaluation", "v2", "cases.jsonl");
    private static EvaluationCase Case(string? text = null)
    {
        text ??= File.ReadLines(Fixture()).First(); using var row = JsonDocument.Parse(text); var root = row.RootElement;
        return new(root.GetProperty("caseId").GetString()!, root.GetProperty("category").GetString()!, text, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant());
    }
    private sealed class Client : IChatClient
    {
        public ChatResponse Reply { get; set; } = new(new ChatMessage(ChatRole.Assistant, "[]"));
        public int Calls { get; private set; }
        public List<(string Text, string[] Tools)> Requests { get; } = [];
        public TimeSpan? ResponseDelay { get; set; }
        public Func<IReadOnlyList<ChatMessage>, ChatResponse>? ReplyFactory { get; set; }
        public ChatClientMetadata Metadata { get; } = new("adapter-test");
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var request = messages.ToArray();
            Calls++; Requests.Add((string.Join("\n", request.Select(m => m.Text)), options?.Tools?.Select(t => t.Name).ToArray() ?? []));
            if (ResponseDelay is { } delay) await Task.Delay(delay, cancellationToken);
            return ReplyFactory?.Invoke(request) ?? Reply;
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null; public void Dispose() { }
    }
    private static (FourArmModelAdapters Adapter, Client Client) Setup(Func<InvestigationLimitOptions, InvestigationLimits>? limitsFactory = null)
    {
        var client = new Client();
        var gate = EvaluationFixtureModelGate.FromCommittedFixtures();
        var adapter = limitsFactory is null
            ? new FourArmModelAdapters(gate, _ => client)
            : new FourArmModelAdapters(gate, _ => client, limitsFactory);
        return (adapter, client);
    }
    [Fact]
    public async Task AC016_UsesIdenticalInputsAndRulesArmMakesNoCalls()
    {
        var (adapter, client) = Setup(); var input = Case(); var b = FakeApprovedManifest.Approved();
        var rulesResult = await adapter.ExecuteAsync(EvaluationArm.A, input, null);
        Assert.Equal(0, client.Calls);
        var results = new[] { rulesResult, await adapter.ExecuteAsync(EvaluationArm.B, input, b), await adapter.ExecuteAsync(EvaluationArm.C, input, b with { Model = "fixture-model-c" }), await adapter.ExecuteAsync(EvaluationArm.D, input, b, b) };
        using var row = JsonDocument.Parse(input.Content); var raw = row.RootElement.GetProperty("inputs").GetRawText();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
        Assert.All(results, r => Assert.Equal((input.CaseId, "evaluation-basis-" + input.CaseId, 2, raw, hash), (r.Basis.CaseId, r.Basis.EvidenceBasisId, r.Basis.Version, r.Basis.InputsJson, r.Basis.InputsSha256)));
        Assert.Equal("""{"caseId":"EVAL-V2-01","assessment":{"state":"ready","missingRequirements":[]},"citations":[]}""", results[0].Answer);
        Assert.Empty(results[0].Claims);
        Assert.Equal((0, 0, 0), (results[0].ModelCalls, results[0].ToolCalls, results[0].CitationReflections));
        var missingCase = Case(File.ReadLines(Fixture()).Skip(10).First());
        var missingResult = await adapter.ExecuteAsync(EvaluationArm.A, missingCase, null);
        using var assessment = JsonDocument.Parse(missingResult.Answer);
        Assert.Equal("request", assessment.RootElement.GetProperty("assessment").GetProperty("state").GetString());
        Assert.Equal("release-certificate", assessment.RootElement.GetProperty("assessment").GetProperty("missingRequirements")[0].GetString());
        Assert.Equal(3, client.Calls);
        await Assert.ThrowsAsync<EvaluationFixtureAuthorizationException>(() => adapter.ExecuteAsync(EvaluationArm.A, Case(input.Content.Replace("\"pageText\":\"", "\"pageText\":\"x", StringComparison.Ordinal)), null));
    }
    [Fact]
    public async Task AC013_AgentArmsUseApprovedManifestAndAllowlist()
    {
        var (adapter, client) = Setup(); var input = Case(); var manifest = FakeApprovedManifest.Approved();
        foreach (var arm in new[] { EvaluationArm.B, EvaluationArm.C })
        { await adapter.ExecuteAsync(arm, input, manifest); Assert.Equal(FourArmModelAdapters.ReadTools.Order().ToArray(), client.Requests[^1].Tools.Order().ToArray()); }
        Assert.Equal((20, 8, TimeSpan.FromSeconds(30)), (FourArmModelAdapters.MaximumToolCalls, FourArmModelAdapters.MaximumModelCalls, FourArmModelAdapters.MaximumDuration));
        var count = client.Calls;
        foreach (var arm in new[] { EvaluationArm.B, EvaluationArm.C })
        { await Assert.ThrowsAsync<ModelGateException>(() => adapter.ExecuteAsync(arm, input, manifest with { Approved = false })); await Assert.ThrowsAsync<EvaluationInputException>(() => adapter.ExecuteAsync(arm, input, null)); }
        Assert.Equal(count, client.Calls);
        client.Reply = new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("x", "unauthorized_tool", new Dictionary<string, object?>())]));
        await Assert.ThrowsAsync<EvaluationFixtureAuthorizationException>(() => adapter.ExecuteAsync(EvaluationArm.B, input, manifest));
        client.Reply = new(new ChatMessage(ChatRole.Assistant, Enumerable.Range(0, 21).Select(i => (AIContent)new FunctionCallContent(i.ToString(), FourArmModelAdapters.ReadTools[0], new Dictionary<string, object?>())).ToList()));
        await Assert.ThrowsAsync<InvestigationLimitExceededException>(() => adapter.ExecuteAsync(EvaluationArm.B, input, manifest)); Assert.Equal(count + 2, client.Calls);

        client.ReplyFactory = messages =>
        {
            var completedTools = messages.Sum(message => message.Contents.OfType<FunctionResultContent>().Count());
            return new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"call-{completedTools}",
                FourArmModelAdapters.ReadTools[0], new Dictionary<string, object?>())]));
        };
        var modelCallsBefore = client.Calls;
        var modelLimit = await Assert.ThrowsAsync<InvestigationLimitExceededException>(() => adapter.ExecuteAsync(EvaluationArm.C, input, manifest));
        Assert.Equal(InvestigationBreach.ModelCalls, modelLimit.Breach);
        Assert.Equal(FourArmModelAdapters.MaximumModelCalls, client.Calls - modelCallsBefore);
        client.ReplyFactory = null;

        var timed = Setup(options => new InvestigationLimits(options with { Timeout = TimeSpan.FromMilliseconds(150) }));
        timed.Client.ResponseDelay = TimeSpan.FromMilliseconds(600);
        var durationLimit = await Assert.ThrowsAsync<InvestigationLimitExceededException>(() => timed.Adapter.ExecuteAsync(EvaluationArm.B, input, manifest));
        Assert.Equal(InvestigationBreach.Time, durationLimit.Breach);
        Assert.Equal(1, timed.Client.Calls);
    }
    [Fact]
    public async Task AC016_ComparatorMatchesArmBAndIsSingleCall()
    {
        var (adapter, client) = Setup(); var b = FakeApprovedManifest.Approved(); var result = await adapter.ExecuteAsync(EvaluationArm.D, Case(), b, b);
        Assert.Equal((1, 0, 0), (result.ModelCalls, result.ToolCalls, result.CitationReflections)); Assert.Equal(1, client.Calls);
        var mismatches = new[] { b with { ManifestVersion = "2026.10.2" }, b with { DeploymentName = "other-deployment" },
            b with { Model = "other-model" }, b with { ModelVersion = "2026-09-23" }, b with { ApiVersion = "v2" },
            b with { Region = "westeurope" }, b with { ContentFilter = "disabled" } };
        foreach (var mismatch in mismatches) await Assert.ThrowsAsync<EvaluationInputException>(() => adapter.ExecuteAsync(EvaluationArm.D, Case(), mismatch, b));
        Assert.Equal(1, client.Calls);
    }
    [Fact]
    public async Task NFR002_UsesSyntheticInputsAndFailsWithoutFallbackClaims()
    {
        var (adapter, client) = Setup(); var input = Case(); using var row = JsonDocument.Parse(input.Content);
        var doc = row.RootElement.GetProperty("inputs").GetProperty("documents")[0]; var page = doc.GetProperty("pageText").GetString()!;
        client.Reply = new(new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(new[] { new { text = "typed claim",
            witnesses = new[] { new { documentId = doc.GetProperty("documentId").GetString(), page = 1, quote = page } } } })));
        var approved = FakeApprovedManifest.Approved();
        var result = await adapter.ExecuteAsync(EvaluationArm.D, input, approved, approved);
        Assert.Equal("typed claim", Assert.Single(result.Claims).Text); Assert.Equal(page, Assert.Single(Assert.Single(result.Claims).Witnesses).Quote);
        var request = Assert.Single(client.Requests).Text;
        using var envelope = JsonDocument.Parse(request.Split('\n').Last());
        Assert.Equal(result.Basis.CaseId, envelope.RootElement.GetProperty("caseId").GetString());
        Assert.Equal(result.Basis.InputsJson, envelope.RootElement.GetProperty("inputs").GetRawText());
        Assert.Equal(result.Basis.InputsSha256, envelope.RootElement.GetProperty("inputsSha256").GetString());
        Assert.DoesNotContain("fixture-deployment", request);
        client.Reply = new(new ChatMessage(ChatRole.Assistant, "malformed"));
        await Assert.ThrowsAsync<EvaluationAdapterOutputException>(() => adapter.ExecuteAsync(EvaluationArm.D, input, approved, approved));
        Assert.Equal(2, client.Calls);
    }
}

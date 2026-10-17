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
        public ChatClientMetadata Metadata { get; } = new("adapter-test");
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++; Requests.Add((string.Join("\n", messages.Select(m => m.Text)), options?.Tools?.Select(t => t.Name).ToArray() ?? [])); return Task.FromResult(Reply);
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null; public void Dispose() { }
    }
    private static (FourArmModelAdapters Adapter, Client Client) Setup() { var client = new Client(); return (new(EvaluationFixtureModelGate.FromCommittedFixtures(), _ => client), client); }
    [Fact]
    public async Task AC016_UsesIdenticalInputsAndRulesArmMakesNoCalls()
    {
        var (adapter, client) = Setup(); var input = Case(); var b = FakeApprovedManifest.Approved();
        var results = new[] { await adapter.ExecuteAsync(EvaluationArm.A, input, null), await adapter.ExecuteAsync(EvaluationArm.B, input, b), await adapter.ExecuteAsync(EvaluationArm.C, input, b with { Model = "fixture-model-c" }), await adapter.ExecuteAsync(EvaluationArm.D, input, b, b) };
        using var row = JsonDocument.Parse(input.Content); var raw = row.RootElement.GetProperty("inputs").GetRawText();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
        Assert.All(results, r => Assert.Equal((input.CaseId, "evaluation-basis-" + input.CaseId, 2, raw, hash), (r.Basis.CaseId, r.Basis.EvidenceBasisId, r.Basis.Version, r.Basis.InputsJson, r.Basis.InputsSha256)));
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
    }
    [Fact]
    public async Task AC016_ComparatorMatchesArmBAndIsSingleCall()
    {
        var (adapter, client) = Setup(); var b = FakeApprovedManifest.Approved(); var result = await adapter.ExecuteAsync(EvaluationArm.D, Case(), b, b);
        Assert.Equal((1, 0, 0), (result.ModelCalls, result.ToolCalls, result.CitationReflections)); Assert.Equal(1, client.Calls);
        var mismatches = new[] { b with { ManifestVersion = "2026.10.2" }, b with { DeploymentName = "other-deployment" },
            b with { Model = "other-model" }, b with { ApiVersion = "2025-02-01" }, b with { Region = "westeurope" }, b with { ContentFilter = "disabled" } };
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

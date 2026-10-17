using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AirlineDemo.Api.Evaluation;
using Microsoft.Extensions.AI;

namespace AirlineDemo.Api.Tests;

public sealed class EvaluationFixtureModelGateTests
{
    private static string Dir([CallerFilePath] string source = "") => Path.Combine(Path.GetDirectoryName(source)!, "Fixtures", "Evaluation", "v2");
    private static string Copy()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fixture-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        foreach (var file in new[] { "manifest.json", "cases.jsonl" })
        {
            File.Copy(Path.Combine(Dir(), file), Path.Combine(directory, file));
        }

        return directory;
    }

    private static EvaluationCase Case(string? content = null)
    {
        content ??= File.ReadLines(Path.Combine(Dir(), "cases.jsonl")).First();
        using var row = JsonDocument.Parse(content);
        return new(row.RootElement.GetProperty("caseId").GetString()!, row.RootElement.GetProperty("category").GetString()!,
            content, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant());
    }

    private sealed class RequestSpy : IChatClient
    {
        public int Requests { get; private set; }
        public ChatClientMetadata Metadata { get; } = new("fixture-gate-test");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Requests++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "[]")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task AC016_AuthorizesOnlyVerifiedV2FixturePages()
    {
        var gate = new EvaluationFixtureModelGate(Dir());
        using var row = JsonDocument.Parse(File.ReadLines(Path.Combine(Dir(), "cases.jsonl")).First());
        var id = row.RootElement.GetProperty("caseId").GetString()!;
        var page = row.RootElement.GetProperty("inputs").GetProperty("documents")[0].GetProperty("pageText").GetString()!;
        gate.AuthorizePageText(id, page);
        Assert.Throws<EvaluationFixtureAuthorizationException>(() => gate.AuthorizePageText(id, page + " changed"));

        var client = new RequestSpy();
        var adapter = new FourArmModelAdapters(gate, _ => client);
        var changedPageCase = Case(Case().Content.Replace("\"pageText\":\"", "\"pageText\":\"changed ", StringComparison.Ordinal));
        await Assert.ThrowsAsync<EvaluationFixtureAuthorizationException>(() =>
            adapter.ExecuteAsync(EvaluationArm.B, changedPageCase, FakeApprovedManifest.Approved()));
        Assert.Equal(0, client.Requests);

        async Task AssertInvalidFixtureMakesNoRequest(Action<string> corrupt)
        {
            var directory = Copy();
            var model = new RequestSpy();
            try
            {
                corrupt(directory);
                await Assert.ThrowsAsync<EvaluationFixtureAuthorizationException>(async () =>
                {
                    var verifiedGate = new EvaluationFixtureModelGate(directory);
                    var verifiedAdapter = new FourArmModelAdapters(verifiedGate, _ => model);
                    await verifiedAdapter.ExecuteAsync(EvaluationArm.B, Case(), FakeApprovedManifest.Approved());
                });
                Assert.Equal(0, model.Requests);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        await AssertInvalidFixtureMakesNoRequest(directory =>
        {
            var path = Path.Combine(directory, "cases.jsonl");
            File.WriteAllText(path, File.ReadAllText(path).Replace("\"inputVersion\":2", "\"inputVersion\":2 ", StringComparison.Ordinal));
        });
        await AssertInvalidFixtureMakesNoRequest(directory =>
        {
            var path = Path.Combine(directory, "manifest.json");
            var manifest = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            manifest.Remove("casesSha256");
            File.WriteAllText(path, manifest.ToJsonString());
        });
        await AssertInvalidFixtureMakesNoRequest(directory =>
        {
            var path = Path.Combine(directory, "manifest.json");
            var manifest = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            manifest["casesSha256"] = new string('0', 64);
            File.WriteAllText(path, manifest.ToJsonString());
        });
        await AssertInvalidFixtureMakesNoRequest(directory =>
        {
            var path = Path.Combine(directory, "manifest.json");
            File.WriteAllText(path, File.ReadAllText(path).Replace("\"fixtureVersion\": 2", "\"fixtureVersion\": 3", StringComparison.Ordinal));
        });
    }
}

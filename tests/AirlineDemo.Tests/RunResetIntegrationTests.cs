using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AirlineDemo.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace AirlineDemo.Tests;

public sealed class RunResetIntegrationTests
{
    [Fact]
    public async Task ResetRun_MissingConfirmationDoesNotMutateState()
    {
        using var fixture = new ResetFixture();
        fixture.WritePackage("RUN-0001", "CASE-0001", "PKG-0001");
        await using var server = await fixture.StartAsync();
        await fixture.LoadAsync(fixture.ClientFor("RUN-0001"), "RUN-0001", "CASE-0001", "PKG-0001");
        var before = await File.ReadAllTextAsync(fixture.StatePath);

        var response = await fixture.ResetAsync(
            fixture.ClientFor("RUN-0001"),
            "RUN-0001",
            new RunResetRequest("RUN-0001", null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await File.ReadAllTextAsync(fixture.StatePath));
    }

    [Fact]
    public async Task ResetRun_MismatchedConfirmationDoesNotMutateState()
    {
        using var fixture = new ResetFixture();
        fixture.WritePackage("RUN-0001", "CASE-0001", "PKG-0001");
        await using var server = await fixture.StartAsync();
        await fixture.LoadAsync(fixture.ClientFor("RUN-0001"), "RUN-0001", "CASE-0001", "PKG-0001");
        var before = await File.ReadAllTextAsync(fixture.StatePath);

        var response = await fixture.ResetAsync(
            fixture.ClientFor("RUN-0001"),
            "RUN-0001",
            new RunResetRequest("RUN-0001", "DELETE"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await File.ReadAllTextAsync(fixture.StatePath));
    }

    [Fact]
    public async Task ResetRun_MissingRunIdDoesNotMutateState()
    {
        using var fixture = new ResetFixture();
        fixture.WritePackage("RUN-0001", "CASE-0001", "PKG-0001");
        await using var server = await fixture.StartAsync();
        await fixture.LoadAsync(fixture.ClientFor("RUN-0001"), "RUN-0001", "CASE-0001", "PKG-0001");
        var before = await File.ReadAllTextAsync(fixture.StatePath);

        var response = await fixture.ResetAsync(
            fixture.ClientFor("RUN-0001"),
            "RUN-0001",
            new RunResetRequest(null, "RESET"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(before, await File.ReadAllTextAsync(fixture.StatePath));
    }

    [Fact]
    public async Task ResetRun_RemovesOnlyNamedRunAndPersistsLoadAndResetReceipts()
    {
        using var fixture = new ResetFixture();
        fixture.WritePackage("RUN-0001", "CASE-0001", "PKG-0001");
        fixture.WritePackage("RUN-0002", "CASE-0002", "PKG-0002");
        var evaluatorPath = Path.Combine(fixture.DirectoryPath, "evaluator-only", "answers.json");
        Directory.CreateDirectory(Path.GetDirectoryName(evaluatorPath)!);
        await File.WriteAllTextAsync(evaluatorPath, "evaluator truth");

        await using var server = await fixture.StartAsync();
        await fixture.LoadAsync(fixture.ClientFor("RUN-0001"), "RUN-0001", "CASE-0001", "PKG-0001");
        await fixture.LoadAsync(fixture.ClientFor("RUN-0002"), "RUN-0002", "CASE-0002", "PKG-0002");
        using var beforeReset = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.StatePath));
        var run2Snapshot = SnapshotRunState(beforeReset.RootElement, "RUN-0002");
        var run1LoadReceipt = beforeReset.RootElement
            .GetProperty("receipts")
            .EnumerateObject()
            .Select(entry => entry.Value)
            .Single(receipt =>
                receipt.GetProperty("runId").GetString() == "RUN-0001" &&
                receipt.GetProperty("operationType").GetString() == "load");
        var selectedManifestSha256 = run1LoadReceipt
            .GetProperty("selectedManifestSha256")
            .GetString();
        Assert.Equal(
            fixture.ExpectedManifestSha256("RUN-0001", "CASE-0001"),
            selectedManifestSha256);
        Assert.Equal("RUN-0001", run1LoadReceipt.GetProperty("runId").GetString());
        Assert.True(
            DateTimeOffset.TryParse(
                run1LoadReceipt.GetProperty("timestamp").GetString(),
                out _));
        Assert.Equal(4, run1LoadReceipt.GetProperty("affectedRecordCount").GetInt32());

        var response = await fixture.ResetAsync(
            fixture.ClientFor("RUN-0001"),
            "RUN-0001",
            new RunResetRequest("RUN-0001", "RESET"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reset = await response.Content.ReadFromJsonAsync<ResetAccepted>();
        Assert.NotNull(reset);
        Assert.Equal("RUN-0001", reset!.RunId);
        Assert.Equal(4, reset.AffectedRecordCount);

        using var afterReset = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.StatePath));
        Assert.Equal(
            run2Snapshot,
            SnapshotRunState(afterReset.RootElement, "RUN-0002"));
        Assert.DoesNotContain(
            afterReset.RootElement.GetProperty("cases").EnumerateObject(),
            entry => entry.Value.GetProperty("context").GetProperty("runId").GetString() == "RUN-0001");
        Assert.DoesNotContain(
            afterReset.RootElement.GetProperty("packages").EnumerateObject(),
            entry => entry.Value.GetProperty("context").GetProperty("runId").GetString() == "RUN-0001");
        Assert.DoesNotContain(
            afterReset.RootElement.GetProperty("operations").EnumerateObject(),
            entry => entry.Value.GetProperty("status").GetProperty("runId").GetString() == "RUN-0001");
        Assert.DoesNotContain(
            afterReset.RootElement.GetProperty("documents").EnumerateObject(),
            entry => entry.Value.GetProperty("context").GetProperty("runId").GetString() == "RUN-0001");
        Assert.Contains(
            afterReset.RootElement.GetProperty("cases").EnumerateObject(),
            entry => entry.Value.GetProperty("context").GetProperty("runId").GetString() == "RUN-0002");
        Assert.Contains(
            afterReset.RootElement.GetProperty("packages").EnumerateObject(),
            entry => entry.Value.GetProperty("context").GetProperty("runId").GetString() == "RUN-0002");
        Assert.Equal("evaluator truth", await File.ReadAllTextAsync(evaluatorPath));
        Assert.Contains(
            afterReset.RootElement.GetProperty("receipts").EnumerateObject(),
            entry =>
                entry.Value.GetProperty("runId").GetString() == "RUN-0001" &&
                entry.Value.GetProperty("operationType").GetString() == "load");

        var resetReceipt = afterReset.RootElement
            .GetProperty("receipts")
            .EnumerateObject()
            .Select(entry => entry.Value)
            .Single(receipt => receipt.GetProperty("receiptId").GetString() == reset.ReceiptId);
        Assert.Equal("reset", resetReceipt.GetProperty("operationType").GetString());
        Assert.Equal("RUN-0001", resetReceipt.GetProperty("runId").GetString());
        Assert.Equal(selectedManifestSha256, resetReceipt.GetProperty("selectedManifestSha256").GetString());
        Assert.True(
            DateTimeOffset.TryParse(
                resetReceipt.GetProperty("timestamp").GetString(),
                out _));
        Assert.Equal(4, resetReceipt.GetProperty("affectedRecordCount").GetInt32());
    }

    private static string SnapshotRunState(JsonElement state, string runId)
    {
        var snapshot = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var category in new[] { "cases", "packages", "operations", "documents", "receipts", "auditReceipts" })
        {
            if (!state.TryGetProperty(category, out var records))
            {
                snapshot[category] = [];
                continue;
            }

            snapshot[category] = records
                .EnumerateObject()
                .Where(entry => GetRecordRunId(category, entry.Value) == runId)
                .OrderBy(entry => entry.Name, StringComparer.Ordinal)
                .Select(entry => $"{entry.Name}:{entry.Value.GetRawText()}")
                .ToArray();
        }

        return JsonSerializer.Serialize(snapshot);
    }

    private static string? GetRecordRunId(string category, JsonElement record)
    {
        return category switch
        {
            "cases" or "packages" or "documents" =>
                record.GetProperty("context").GetProperty("runId").GetString(),
            "operations" =>
                record.GetProperty("status").GetProperty("runId").GetString(),
            "receipts" or "auditReceipts" =>
                record.GetProperty("runId").GetString(),
            _ => null
        };
    }

    private sealed class ResetFixture : IDisposable
    {
        private readonly string directory = Path.Combine(
            Path.GetTempPath(), "airlinedemo-run-reset-tests", Guid.NewGuid().ToString("N"));
        private ApiServer? server;

        public string DirectoryPath => directory;
        public string StatePath => Path.Combine(directory, "workflow-state.json");

        public void WritePackage(string runId, string caseId, string packageId)
        {
            var documentDirectory = Path.Combine(
                directory, "documents", runId, caseId, $"DOC-{runId[^1]}", "v1");
            Directory.CreateDirectory(documentDirectory);
            File.WriteAllText(
                Path.Combine(documentDirectory, "record.pdf"),
                $"fixture document for {runId}");
        }

        public async Task<ApiServer> StartAsync()
        {
            var app = WorkflowApi.Create(directory);
            server = new ApiServer(app);
            await server.App.StartAsync();
            return server;
        }

        public async Task LoadAsync(
            HttpClient client,
            string runId,
            string caseId,
            string packageId)
        {
            var documentPath = Path.Combine(
                directory, "documents", runId, caseId, $"DOC-{runId[^1]}", "v1", "record.pdf");
            var content = await File.ReadAllBytesAsync(documentPath);
            var document = new DocumentMetadata(
                $"DOC-{runId[^1]}",
                1,
                "fixture-generator",
                $"SOURCE-{runId[^1]}",
                "record.pdf",
                "application/pdf",
                Convert.ToHexString(SHA256.HashData(content)),
                DateTimeOffset.Parse("2026-09-10T12:00:00Z"));
            using var payload = JsonDocument.Parse($$"""{"packageId":"{{packageId}}"}""");
            var package = new SubmissionPackage(
                "1.0",
                packageId,
                runId,
                caseId,
                $"AIRLINE-{runId[^1]}",
                $"MOCK-AC-00{runId[^1]}",
                $"LEASE-000{runId[^1]}",
                DateTimeOffset.Parse("2026-09-10T13:00:00Z"),
                DateTimeOffset.Parse("2026-09-10T09:00:00Z"),
                [document]);
            var request = new PackageSubmissionRequest(
                new EventEnvelope(
                    "1.0",
                    $"EVT-{runId[^1]}",
                    "package.submitted",
                    runId,
                    caseId,
                    package.AirlineId,
                    package.AircraftId,
                    package.LeaseId,
                    package.SubmittedAt,
                    package.ScenarioEffectiveAt,
                    $"CORR-{runId[^1]}",
                    payload.RootElement.Clone()),
                package);
            var response = await client.PostAsJsonAsync("/api/packages", request);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        public string ExpectedManifestSha256(string runId, string caseId)
        {
            var documentPath = Path.Combine(
                directory, "documents", runId, caseId, $"DOC-{runId[^1]}", "v1", "record.pdf");
            var document = new DocumentMetadata(
                $"DOC-{runId[^1]}",
                1,
                "fixture-generator",
                $"SOURCE-{runId[^1]}",
                "record.pdf",
                "application/pdf",
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(documentPath))),
                DateTimeOffset.Parse("2026-09-10T12:00:00Z"));
            var json = JsonSerializer.SerializeToUtf8Bytes(
                new[] { document },
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
                {
                    WriteIndented = false
                });
            return Convert.ToHexString(SHA256.HashData(json));
        }

        public Task<HttpResponseMessage> ResetAsync(
            HttpClient client,
            string runId,
            RunResetRequest request) =>
            client.SendAsync(new HttpRequestMessage(
                HttpMethod.Delete,
                $"/api/demo/runs/{runId}")
            {
                Content = JsonContent.Create(request)
            });

        public HttpClient ClientFor(string runId) =>
            new()
            {
                BaseAddress = server!.BaseAddress,
                DefaultRequestHeaders =
                {
                    Authorization = new AuthenticationHeaderValue(
                        "Bearer",
                        $"run={runId};airline=AIRLINE-{runId[^1]};aircraft=MOCK-AC-00{runId[^1]};lease=LEASE-000{runId[^1]}")
                }
            };

        public void Dispose()
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed class ApiServer(WebApplication app) : IAsyncDisposable
    {
        public WebApplication App { get; } = app;
        public Uri BaseAddress =>
            new(App.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single());

        public ValueTask DisposeAsync() => App.DisposeAsync();
    }
}

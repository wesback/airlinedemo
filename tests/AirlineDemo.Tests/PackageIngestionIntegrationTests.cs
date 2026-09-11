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

public sealed class PackageIngestionIntegrationTests
{
    [Fact]
    public async Task PackageSubmitted_PersistsOneReceiptAndQueuedOperation()
    {
        using var fixture = new IngestionFixture();
        var content = fixture.WriteDocument();

        await using var server = await fixture.StartAsync();
        var response = await fixture.PostAsync(server.Client, fixture.CreateRequest(content.Hash));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.Content.ReadFromJsonAsync<OperationAccepted>();
        Assert.NotNull(accepted);
        Assert.False(string.IsNullOrWhiteSpace(accepted.ReceiptId));

        var operationResponse = await server.Client.GetAsync($"/api/operations/{accepted.OperationId}");
        var operation = await operationResponse.Content.ReadFromJsonAsync<OperationStatus>();
        Assert.NotNull(operation);
        Assert.Equal("queued", operation.Status);

        using var state = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.StatePath));
        var receipt = state.RootElement.GetProperty("receipts").EnumerateObject().Single().Value;
        Assert.Equal(accepted.ReceiptId, receipt.GetProperty("receiptId").GetString());
        Assert.Single(state.RootElement.GetProperty("operations").EnumerateObject());
    }

    [Fact]
    public async Task PackageSubmitted_ReplaysReceiptAndConflictingPayloadDoesNotMutate()
    {
        using var fixture = new IngestionFixture();
        var content = fixture.WriteDocument();
        var request = fixture.CreateRequest(content.Hash);

        await using var server = await fixture.StartAsync();
        var first = await fixture.PostAsync(server.Client, request);
        var firstAccepted = await first.Content.ReadFromJsonAsync<OperationAccepted>();
        Assert.NotNull(firstAccepted);
        var stateAfterFirst = await File.ReadAllTextAsync(fixture.StatePath);

        var replay = await fixture.PostAsync(server.Client, request);
        var replayAccepted = await replay.Content.ReadFromJsonAsync<OperationAccepted>();
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        Assert.NotNull(replayAccepted);
        Assert.Equal(firstAccepted.OperationId, replayAccepted.OperationId);
        Assert.Equal(firstAccepted.CaseId, replayAccepted.CaseId);
        Assert.Equal(firstAccepted.ReceiptId, replayAccepted.ReceiptId);
        Assert.Equal(stateAfterFirst, await File.ReadAllTextAsync(fixture.StatePath));
        var stateBeforeConflict = await File.ReadAllTextAsync(fixture.StatePath);

        var conflictingRequest = request with
        {
            Event = request.Event with { CorrelationId = "CORR-0002" }
        };
        var conflict = await fixture.PostAsync(server.Client, conflictingRequest);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(stateBeforeConflict, await File.ReadAllTextAsync(fixture.StatePath));

        using var state = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.StatePath));
        var receipts = state.RootElement.GetProperty("receipts").EnumerateObject().ToArray();
        Assert.Single(receipts);
        Assert.Equal(
            firstAccepted.ReceiptId,
            receipts[0].Value.GetProperty("receiptId").GetString());
        Assert.Single(state.RootElement.GetProperty("packages").EnumerateObject());
        Assert.Single(state.RootElement.GetProperty("operations").EnumerateObject());
        Assert.False(state.RootElement.TryGetProperty("auditReceipts", out _));
    }

    [Fact]
    public async Task PackageSubmitted_RejectsInvalidManifestInputsBeforeQueueing()
    {
        using var fixture = new IngestionFixture();
        var content = fixture.WriteDocument();
        await using var server = await fixture.StartAsync();

        var extraPath = Path.Combine(
            fixture.DocumentDirectory,
            "undeclared.txt");
        await File.WriteAllTextAsync(extraPath, "not in manifest");
        var undeclared = await fixture.PostAsync(
            server.Client, fixture.CreateRequest(content.Hash, eventId: "EVT-UNDECLARED"));
        Assert.Equal(HttpStatusCode.BadRequest, undeclared.StatusCode);
        File.Delete(extraPath);

        var mismatch = await fixture.PostAsync(
            server.Client,
            fixture.CreateRequest(new string('a', 64), eventId: "EVT-MISMATCH"));
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);

        var invalidScope = fixture.CreateRequest(
            content.Hash,
            eventId: "EVT-SCOPE",
            caseId: "CASE/INVALID");
        var invalidScopeResponse = await fixture.PostAsync(server.Client, invalidScope);
        Assert.Equal(HttpStatusCode.BadRequest, invalidScopeResponse.StatusCode);

        var unsupportedVersion = fixture.CreateRequest(
            content.Hash,
            eventId: "EVT-VERSION",
            schemaVersion: "2.0");
        var unsupportedVersionResponse = await fixture.PostAsync(
            server.Client, unsupportedVersion);
        Assert.Equal(HttpStatusCode.BadRequest, unsupportedVersionResponse.StatusCode);

        var outsideStorage = fixture.CreateRequest(
            content.Hash,
            eventId: "EVT-PATH",
            fileName: "../outside.pdf");
        var outsideStorageResponse = await fixture.PostAsync(
            server.Client, outsideStorage);
        Assert.Equal(HttpStatusCode.BadRequest, outsideStorageResponse.StatusCode);

        Assert.False(File.Exists(fixture.StatePath));
    }

    private sealed class IngestionFixture : IDisposable
    {
        private readonly string directory = Path.Combine(
            Path.GetTempPath(), "airlinedemo-ingestion-tests", Guid.NewGuid().ToString("N"));
        private ApiServer? server;

        public string StatePath => Path.Combine(directory, "workflow-state.json");

        public string DocumentDirectory => Path.Combine(
            directory, "documents", "RUN-0001", "CASE-0001", "DOC-0001", "v1");

        public (string Hash, byte[] Content) WriteDocument()
        {
            var content = "manifest-backed test document"u8.ToArray();
            Directory.CreateDirectory(DocumentDirectory);
            File.WriteAllBytes(Path.Combine(DocumentDirectory, "maintenance-record.pdf"), content);
            return (Convert.ToHexString(SHA256.HashData(content)), content);
        }

        public PackageSubmissionRequest CreateRequest(
            string hash,
            string eventId = "EVT-0001",
            string caseId = "CASE-0001",
            string fileName = "maintenance-record.pdf",
            string schemaVersion = "1.0")
        {
            using var payload = JsonDocument.Parse($$"""{"packageId":"PKG-0001"}""");
            var eventEnvelope = new EventEnvelope(
                schemaVersion,
                eventId,
                "package.submitted",
                "RUN-0001",
                caseId,
                "AIRLINE-0001",
                "MOCK-AC-001",
                "LEASE-0001",
                DateTimeOffset.Parse("2026-09-10T13:00:00Z"),
                DateTimeOffset.Parse("2026-09-10T09:00:00Z"),
                "CORR-0001",
                payload.RootElement.Clone());
            var package = new SubmissionPackage(
                schemaVersion,
                "PKG-0001",
                "RUN-0001",
                caseId,
                "AIRLINE-0001",
                "MOCK-AC-001",
                "LEASE-0001",
                DateTimeOffset.Parse("2026-09-10T13:00:00Z"),
                DateTimeOffset.Parse("2026-09-10T09:00:00Z"),
                [
                    new DocumentMetadata(
                        "DOC-0001",
                        1,
                        "fixture-generator",
                        "SOURCE-0001",
                        fileName,
                        "application/pdf",
                        hash,
                        DateTimeOffset.Parse("2026-09-10T12:00:00Z"))
                ]);
            return new PackageSubmissionRequest(eventEnvelope, package);
        }

        public async Task<ApiServer> StartAsync()
        {
            var app = WorkflowApi.Create(directory);
            server = new ApiServer(app);
            await server.App.StartAsync();
            server.Client = CreateClient();
            return server;
        }

        public HttpClient CreateClient()
        {
            var client = new HttpClient { BaseAddress = server!.BaseAddress };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                "run=RUN-0001;airline=AIRLINE-0001;aircraft=MOCK-AC-001;lease=LEASE-0001");
            return client;
        }

        public Task<HttpResponseMessage> PostAsync(
            HttpClient client,
            PackageSubmissionRequest request) =>
            client.PostAsJsonAsync("/api/packages", request);

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
        public HttpClient Client { get; set; } = null!;
        public Uri BaseAddress =>
            new(App.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single());

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.DisposeAsync();
        }
    }
}

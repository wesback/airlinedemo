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

    [Fact]
    public async Task QueuedPackage_CompletesOnlyAfterEveryDeclaredFileIsAccountedFor()
    {
        using var fixture = new IngestionFixture();
        var content = fixture.WriteDocument();

        await using var server = await fixture.StartAsync();
        var accepted = await fixture.PostAsync(server.Client, fixture.CreateRequest(content.Hash));
        var operation = await accepted.Content.ReadFromJsonAsync<OperationAccepted>();
        Assert.NotNull(operation);

        var processing = await fixture.ProcessAsync(server.Client, operation!.OperationId);
        Assert.Equal(HttpStatusCode.Accepted, processing.StatusCode);

        var statusResponse = await server.Client.GetAsync(
            $"/api/operations/{operation!.OperationId}");
        var status = await statusResponse.Content.ReadFromJsonAsync<OperationStatus>();
        Assert.NotNull(status);
        Assert.Equal("complete", status.Status);
        Assert.Null(status.Error);
        Assert.Single(status.Attempts!);
        Assert.Equal("complete", status!.Attempts![0].Status);

        var caseResponse = await server.Client.GetAsync("/api/cases/CASE-0001");
        var summary = await caseResponse.Content.ReadFromJsonAsync<CaseSummary>();
        Assert.NotNull(summary);
        Assert.Equal("complete", summary.PackageProcessing.Single().Status);
    }

    [Fact]
    public async Task QueuedPackage_HashMismatchRecordsFailedOperation()
    {
        using var fixture = new IngestionFixture();
        var content = fixture.WriteDocument();

        await using var server = await fixture.StartAsync();
        var accepted = await fixture.PostAsync(server.Client, fixture.CreateRequest(content.Hash));
        var operation = await accepted.Content.ReadFromJsonAsync<OperationAccepted>();
        Assert.NotNull(operation);
        await File.WriteAllTextAsync(fixture.DocumentPath, "changed after submission");

        await fixture.ProcessAsync(server.Client, operation!.OperationId);
        var status = await fixture.GetOperationAsync(server.Client, operation!.OperationId);
        Assert.Equal("failed", status.Status);
        Assert.Equal("INVALID_PAYLOAD", status.Error!.SafeCode);
        Assert.Single(status.Attempts!);
        Assert.Equal("failed", status.Attempts![0].Status);
    }

    [Fact]
    public async Task QueuedPackage_UnreadableDeclaredFileRecordsFailedOperation()
    {
        using var fixture = new IngestionFixture();
        var content = fixture.WriteDocument();
        var storage = new ToggleManifestStorage(fixture.DocumentsRoot);

        await using var server = await fixture.StartAsync(storage);
        var accepted = await fixture.PostAsync(server.Client, fixture.CreateRequest(content.Hash));
        var operation = await accepted.Content.ReadFromJsonAsync<OperationAccepted>();
        Assert.NotNull(operation);
        storage.FailValidation = true;

        await fixture.ProcessAsync(server.Client, operation!.OperationId);
        var status = await fixture.GetOperationAsync(server.Client, operation!.OperationId);
        Assert.Equal("failed", status.Status);
        Assert.Equal("INVALID_PAYLOAD", status.Error!.SafeCode);
        Assert.Single(status.Attempts!);
        Assert.Equal("failed", status.Attempts![0].Status);
    }

    [Fact]
    public async Task QueuedPackage_OmittedDeclaredFileRecordsFailedOperation()
    {
        using var fixture = new IngestionFixture();
        var content = fixture.WriteDocument();

        await using var server = await fixture.StartAsync();
        var accepted = await fixture.PostAsync(server.Client, fixture.CreateRequest(content.Hash));
        var operation = await accepted.Content.ReadFromJsonAsync<OperationAccepted>();
        Assert.NotNull(operation);
        File.Delete(fixture.DocumentPath);

        await fixture.ProcessAsync(server.Client, operation!.OperationId);
        var status = await fixture.GetOperationAsync(server.Client, operation.OperationId);
        Assert.Equal("failed", status.Status);
        Assert.Equal("INVALID_PAYLOAD", status.Error!.SafeCode);
        Assert.Single(status.Attempts!);
        Assert.Equal("failed", status.Attempts![0].Status);
    }

    [Fact]
    public async Task FailedPackage_RetryPreservesFailedAttemptAndReceipt()
    {
        using var fixture = new IngestionFixture();
        var content = fixture.WriteDocument();

        await using var server = await fixture.StartAsync();
        var accepted = await fixture.PostAsync(server.Client, fixture.CreateRequest(content.Hash));
        var original = await accepted.Content.ReadFromJsonAsync<OperationAccepted>();
        Assert.NotNull(original);
        var originalOperationId = original!.OperationId;
        File.Delete(fixture.DocumentPath);

        var failed = await fixture.ProcessAsync(server.Client, originalOperationId);
        Assert.Equal(HttpStatusCode.Accepted, failed.StatusCode);
        var failedStatus = await fixture.GetOperationAsync(server.Client, originalOperationId);
        Assert.Equal("failed", failedStatus.Status);
        Assert.Equal("INVALID_PAYLOAD", failedStatus.Error!.SafeCode);
        Assert.Single(failedStatus.Attempts!);
        Assert.Equal("failed", failedStatus.Attempts![0].Status);

        Directory.CreateDirectory(fixture.DocumentDirectory);
        await File.WriteAllBytesAsync(fixture.DocumentPath, content.Content);
        var retry = await fixture.RetryAsync(server.Client, originalOperationId);
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        var retryAccepted = await retry.Content.ReadFromJsonAsync<OperationAccepted>();
        Assert.NotNull(retryAccepted);
        Assert.Equal(originalOperationId, retryAccepted!.OperationId);
        Assert.Equal(original.ReceiptId, retryAccepted.ReceiptId);

        var retrying = await fixture.GetOperationAsync(server.Client, originalOperationId);
        Assert.Equal("processing", retrying.Status);
        Assert.Equal(2, retrying.Attempts!.Count);
        Assert.Equal("failed", retrying.Attempts[0].Status);
        Assert.Equal("processing", retrying.Attempts[1].Status);
        await fixture.ProcessAsync(server.Client, originalOperationId);

        var completed = await fixture.GetOperationAsync(server.Client, originalOperationId);
        Assert.Equal("complete", completed.Status);
        Assert.Equal(2, completed.Attempts!.Count);
        Assert.Equal("failed", completed.Attempts[0].Status);
        Assert.Equal("complete", completed.Attempts[1].Status);

        using var state = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.StatePath));
        Assert.Single(state.RootElement.GetProperty("receipts").EnumerateObject());
        Assert.Single(state.RootElement.GetProperty("packages").EnumerateObject());
        Assert.Single(state.RootElement.GetProperty("operations").EnumerateObject());
    }

    [Fact]
    public async Task RestartedService_RestoresProcessingAttemptAndDoesNotDuplicateReceipt()
    {
        using var fixture = new IngestionFixture();
        var content = fixture.WriteDocument();
        string operationId;
        string receiptId;

        await using (var server = await fixture.StartAsync())
        {
            var accepted = await fixture.PostAsync(
                server.Client, fixture.CreateRequest(content.Hash));
            var operation = await accepted.Content.ReadFromJsonAsync<OperationAccepted>();
            Assert.NotNull(operation);
            operationId = operation.OperationId;
            receiptId = operation.ReceiptId;

            File.Delete(fixture.DocumentPath);
            await fixture.ProcessAsync(server.Client, operationId);
            var failed = await fixture.GetOperationAsync(server.Client, operationId);
            Assert.Equal("failed", failed.Status);
            Assert.Single(failed.Attempts!);

            Directory.CreateDirectory(fixture.DocumentDirectory);
            await File.WriteAllBytesAsync(fixture.DocumentPath, content.Content);
            var retry = await fixture.RetryAsync(server.Client, operationId);
            Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
            var retryAccepted = await retry.Content.ReadFromJsonAsync<OperationAccepted>();
            Assert.NotNull(retryAccepted);
            Assert.Equal(operationId, retryAccepted.OperationId);
            Assert.Equal(receiptId, retryAccepted.ReceiptId);
        }

        await using (var restarted = await fixture.StartAsync())
        {
            var restored = await fixture.GetOperationAsync(restarted.Client, operationId);
            Assert.Equal("processing", restored.Status);
            Assert.Equal(2, restored.Attempts!.Count);
            Assert.Equal("failed", restored.Attempts[0].Status);
            Assert.Equal("processing", restored.Attempts[1].Status);

            await fixture.ProcessAsync(restarted.Client, operationId);
            var completed = await fixture.GetOperationAsync(restarted.Client, operationId);
            Assert.Equal("complete", completed.Status);
            Assert.Equal(2, completed.Attempts!.Count);
            Assert.Equal("failed", completed.Attempts[0].Status);
            Assert.Equal("complete", completed.Attempts[1].Status);

            var replay = await fixture.PostAsync(
                restarted.Client, fixture.CreateRequest(content.Hash));
            var replayAccepted = await replay.Content.ReadFromJsonAsync<OperationAccepted>();
            Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
            Assert.NotNull(replayAccepted);
            Assert.Equal(operationId, replayAccepted.OperationId);
            Assert.Equal(receiptId, replayAccepted.ReceiptId);
        }

        using var finalState = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.StatePath));
        Assert.Single(finalState.RootElement.GetProperty("receipts").EnumerateObject());
        Assert.Single(finalState.RootElement.GetProperty("packages").EnumerateObject());
        Assert.Single(finalState.RootElement.GetProperty("operations").EnumerateObject());
    }

    [Fact]
    public async Task RestartedService_RestoresQueuedPackageAndDoesNotDuplicateReceipt()
    {
        using var fixture = new IngestionFixture();
        var content = fixture.WriteDocument();
        string operationId;
        string receiptId;

        await using (var server = await fixture.StartAsync())
        {
            var accepted = await fixture.PostAsync(
                server.Client, fixture.CreateRequest(content.Hash));
            var operation = await accepted.Content.ReadFromJsonAsync<OperationAccepted>();
            Assert.NotNull(operation);
            operationId = operation.OperationId;
            receiptId = operation.ReceiptId;
        }

        await using (var restarted = await fixture.StartAsync())
        {
            var restored = await fixture.GetOperationAsync(restarted.Client, operationId);
            Assert.Equal("queued", restored.Status);
            Assert.Null(restored.Attempts);

            var replay = await fixture.PostAsync(
                restarted.Client, fixture.CreateRequest(content.Hash));
            var replayAccepted = await replay.Content.ReadFromJsonAsync<OperationAccepted>();
            Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
            Assert.NotNull(replayAccepted);
            Assert.Equal(operationId, replayAccepted.OperationId);
            Assert.Equal(receiptId, replayAccepted.ReceiptId);

            await fixture.ProcessAsync(restarted.Client, operationId);
            var completed = await fixture.GetOperationAsync(restarted.Client, operationId);
            Assert.Equal("complete", completed.Status);
            Assert.Single(completed.Attempts!);
        }

        using var finalState = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.StatePath));
        Assert.Single(finalState.RootElement.GetProperty("receipts").EnumerateObject());
        Assert.Single(finalState.RootElement.GetProperty("packages").EnumerateObject());
        Assert.Single(finalState.RootElement.GetProperty("operations").EnumerateObject());
    }

    [Fact]
    public async Task RestartedService_RestoresFailedAttemptAndRetryPreservesReceipt()
    {
        using var fixture = new IngestionFixture();
        var content = fixture.WriteDocument();
        string operationId;
        string receiptId;

        await using (var server = await fixture.StartAsync())
        {
            var accepted = await fixture.PostAsync(
                server.Client, fixture.CreateRequest(content.Hash));
            var operation = await accepted.Content.ReadFromJsonAsync<OperationAccepted>();
            Assert.NotNull(operation);
            operationId = operation.OperationId;
            receiptId = operation.ReceiptId;

            File.Delete(fixture.DocumentPath);
            await fixture.ProcessAsync(server.Client, operationId);
            var failed = await fixture.GetOperationAsync(server.Client, operationId);
            Assert.Equal("failed", failed.Status);
            Assert.Single(failed.Attempts!);
            Assert.Equal("failed", failed.Attempts![0].Status);
        }

        await using (var restarted = await fixture.StartAsync())
        {
            var restored = await fixture.GetOperationAsync(restarted.Client, operationId);
            Assert.Equal("failed", restored.Status);
            Assert.Single(restored.Attempts!);
            Assert.Equal("failed", restored.Attempts![0].Status);
            Assert.Equal("INVALID_PAYLOAD", restored.Error!.SafeCode);

            Directory.CreateDirectory(fixture.DocumentDirectory);
            await File.WriteAllBytesAsync(fixture.DocumentPath, content.Content);
            var retry = await fixture.RetryAsync(restarted.Client, operationId);
            var retryAccepted = await retry.Content.ReadFromJsonAsync<OperationAccepted>();
            Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
            Assert.NotNull(retryAccepted);
            Assert.Equal(operationId, retryAccepted.OperationId);
            Assert.Equal(receiptId, retryAccepted.ReceiptId);

            var processing = await fixture.GetOperationAsync(restarted.Client, operationId);
            Assert.Equal("processing", processing.Status);
            Assert.Equal(2, processing.Attempts!.Count);
            Assert.Equal("failed", processing.Attempts[0].Status);
            Assert.Equal("processing", processing.Attempts[1].Status);
        }

        using var finalState = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.StatePath));
        Assert.Single(finalState.RootElement.GetProperty("receipts").EnumerateObject());
        Assert.Single(finalState.RootElement.GetProperty("packages").EnumerateObject());
        Assert.Single(finalState.RootElement.GetProperty("operations").EnumerateObject());
    }

    private sealed class IngestionFixture : IDisposable
    {
        private readonly string directory = Path.Combine(
            Path.GetTempPath(), "airlinedemo-ingestion-tests", Guid.NewGuid().ToString("N"));
        private ApiServer? server;

        public string StatePath => Path.Combine(directory, "workflow-state.json");

        public string DocumentDirectory => Path.Combine(
            directory, "documents", "RUN-0001", "CASE-0001", "DOC-0001", "v1");

        public string DocumentsRoot => Path.Combine(directory, "documents");

        public string DocumentPath => Path.Combine(
            DocumentDirectory, "maintenance-record.pdf");

        public (string Hash, byte[] Content) WriteDocument()
        {
            var content = "manifest-backed test document"u8.ToArray();
            Directory.CreateDirectory(DocumentDirectory);
            File.WriteAllBytes(DocumentPath, content);
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

        public async Task<ApiServer> StartAsync(IDocumentStorage? storage = null)
        {
            var app = WorkflowApi.Create(directory, storage);
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

        public Task<HttpResponseMessage> ProcessAsync(HttpClient client, string operationId) =>
            client.PostAsync($"/api/operations/{operationId}/process", null);

        public Task<HttpResponseMessage> RetryAsync(HttpClient client, string operationId) =>
            client.PostAsync($"/api/operations/{operationId}/retry", null);

        public async Task<OperationStatus> GetOperationAsync(
            HttpClient client,
            string operationId)
        {
            var response = await client.GetAsync($"/api/operations/{operationId}");
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<OperationStatus>())!;
        }

        public void Dispose()
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed class ToggleManifestStorage(string rootDirectory) :
        IDocumentStorage, IManifestDocumentStorage
    {
        private readonly FileDocumentStorage inner = new(rootDirectory);

        public bool FailValidation { get; set; }

        public Task<string?> ReadPagePreviewAsync(
            string storageLocator,
            int page,
            CancellationToken cancellationToken) =>
            inner.ReadPagePreviewAsync(storageLocator, page, cancellationToken);

        public Task<string?> ValidateManifestAsync(
            CaseContext context,
            IReadOnlyList<DocumentMetadata> manifest,
            CancellationToken cancellationToken)
        {
            if (FailValidation)
            {
                throw new IOException("simulated unreadable document");
            }

            return inner.ValidateManifestAsync(context, manifest, cancellationToken);
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

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AirlineDemo.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace AirlineDemo.Tests;

public sealed class ScopedStateIntegrationTests
{
    [Fact]
    public async Task RestartedService_PreservesScopedCasePackageDocumentsOperationAndRevision()
    {
        using var fixture = new ApiFixture();
        var request = fixture.CreateRequest();
        string persistedBeforeRestart;

        await using (var firstService = await fixture.StartAsync())
        {
            var accepted = await fixture.PostPackageAsync(firstService.Client, request);
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            var operation = await accepted.Content.ReadFromJsonAsync<OperationAccepted>();
            Assert.NotNull(operation);
            fixture.OperationId = operation.OperationId;

            var caseResponse = await fixture.GetCaseAsync(firstService.Client, request.Package.CaseId);
            Assert.Equal(HttpStatusCode.OK, caseResponse.StatusCode);
            var summary = await caseResponse.Content.ReadFromJsonAsync<CaseSummary>();
            Assert.NotNull(summary);
            Assert.Equal(request.Package.RunId, summary.RunId);
            Assert.Equal(request.Package.AirlineId, summary.AirlineId);
            Assert.Equal(request.Package.AircraftId, summary.AircraftId);
            Assert.Equal(request.Package.LeaseId, summary.LeaseId);
            Assert.Equal(1, summary.CaseRevision);
            Assert.Equal(operation.OperationId, summary.PackageProcessing.Single().OperationId);

            persistedBeforeRestart = await File.ReadAllTextAsync(fixture.StatePath);
            using var stateBeforeRestart = JsonDocument.Parse(persistedBeforeRestart);
            AssertPersistedRecords(stateBeforeRestart.RootElement, request, operation.OperationId);
        }

        await using (var restartedService = await fixture.StartAsync())
        {
            var operationResponse = await restartedService.Client.GetAsync(
                $"/api/operations/{fixture.OperationId}");
            Assert.Equal(HttpStatusCode.OK, operationResponse.StatusCode);
            var operation = await operationResponse.Content.ReadFromJsonAsync<OperationStatus>();
            Assert.NotNull(operation);
            Assert.Equal(fixture.OperationId, operation.OperationId);
            Assert.Equal(request.Package.RunId, operation.RunId);
            Assert.Equal(request.Package.AirlineId, operation.AirlineId);
            Assert.Equal(request.Package.AircraftId, operation.AircraftId);
            Assert.Equal(request.Package.LeaseId, operation.LeaseId);
            Assert.Equal("queued", operation.Status);
            Assert.Null(operation.Error);

            var caseResponse = await fixture.GetCaseAsync(
                restartedService.Client, request.Package.CaseId);
            var summary = await caseResponse.Content.ReadFromJsonAsync<CaseSummary>();
            Assert.NotNull(summary);
            Assert.Equal(1, summary.CaseRevision);
            Assert.Equal("queued", summary.PackageProcessing.Single().Status);

            var previewResponse = await fixture.GetEvidenceAsync(
                restartedService.Client, request.Package.CaseId, "DOC-0001", 1, 1);
            Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
            var preview = await previewResponse.Content.ReadFromJsonAsync<EvidencePreview>();
            Assert.NotNull(preview);
            Assert.Equal("DOC-0001", preview.DocumentId);
            Assert.Equal(1, preview.Version);
            Assert.Equal("page one preview", preview.TextExcerpt);
        }

        var persisted = await File.ReadAllTextAsync(fixture.StatePath);
        Assert.Equal(persistedBeforeRestart, persisted);
        Assert.Contains("\"runId\": \"RUN-0001\"", persisted);
        Assert.Contains("\"airlineId\": \"AIRLINE-0001\"", persisted);
        Assert.Contains("\"aircraftId\": \"MOCK-AC-001\"", persisted);
        Assert.Contains("\"leaseId\": \"LEASE-0001\"", persisted);
        using var state = JsonDocument.Parse(persisted);
        AssertPersistedRecords(state.RootElement, request, fixture.OperationId);
        var package = state.RootElement.GetProperty("packages").EnumerateObject().Single().Value;
        Assert.Equal("RUN-0001", package.GetProperty("context").GetProperty("runId").GetString());
        Assert.Equal("CASE-0001", package.GetProperty("context").GetProperty("caseId").GetString());
        Assert.Equal("AIRLINE-0001", package.GetProperty("context").GetProperty("airlineId").GetString());
        Assert.Equal("MOCK-AC-001", package.GetProperty("context").GetProperty("aircraftId").GetString());
        Assert.Equal("LEASE-0001", package.GetProperty("context").GetProperty("leaseId").GetString());
        var persistedOperation = state.RootElement.GetProperty("operations")
            .GetProperty(fixture.OperationId).GetProperty("status");
        Assert.Equal("RUN-0001", persistedOperation.GetProperty("runId").GetString());
        Assert.Equal("CASE-0001", persistedOperation.GetProperty("caseId").GetString());
        Assert.Equal("AIRLINE-0001", persistedOperation.GetProperty("airlineId").GetString());
        Assert.Equal("MOCK-AC-001", persistedOperation.GetProperty("aircraftId").GetString());
        Assert.Equal("LEASE-0001", persistedOperation.GetProperty("leaseId").GetString());
        var document = state.RootElement.GetProperty("documents").EnumerateObject().Single().Value;
        Assert.Equal("DOC-0001", document.GetProperty("metadata").GetProperty("documentId").GetString());
        Assert.Equal(1, document.GetProperty("metadata").GetProperty("version").GetInt32());
        Assert.Equal("RUN-0001", document.GetProperty("context").GetProperty("runId").GetString());
        Assert.Equal("CASE-0001", document.GetProperty("context").GetProperty("caseId").GetString());
        Assert.Equal("AIRLINE-0001", document.GetProperty("context").GetProperty("airlineId").GetString());
        Assert.Equal("MOCK-AC-001", document.GetProperty("context").GetProperty("aircraftId").GetString());
        Assert.Equal("LEASE-0001", document.GetProperty("context").GetProperty("leaseId").GetString());
    }

    private static void AssertPersistedRecords(
        JsonElement state,
        PackageSubmissionRequest request,
        string operationId)
    {
        var expectedPackage = request.Package;
        var expectedDocument = expectedPackage.Manifest.Single();
        var persistedCase = state.GetProperty("cases").EnumerateObject().Single().Value;
        AssertScope(persistedCase.GetProperty("context"), expectedPackage);
        Assert.Equal(1, persistedCase.GetProperty("caseRevision").GetInt64());
        Assert.Equal("active", persistedCase.GetProperty("status").GetString());

        var persistedPackage = state.GetProperty("packages").EnumerateObject().Single().Value;
        AssertScope(persistedPackage.GetProperty("context"), expectedPackage);
        var package = persistedPackage.GetProperty("package");
        Assert.Equal(expectedPackage.SchemaVersion, package.GetProperty("schemaVersion").GetString());
        Assert.Equal(expectedPackage.PackageId, package.GetProperty("packageId").GetString());
        AssertScope(package, expectedPackage);
        Assert.Equal(expectedPackage.SubmittedAt, package.GetProperty("submittedAt").GetDateTimeOffset());
        Assert.Equal(
            expectedPackage.ScenarioEffectiveAt,
            package.GetProperty("scenarioEffectiveAt").GetDateTimeOffset());
        var manifest = package.GetProperty("manifest").EnumerateArray().Single();
        Assert.Equal(expectedDocument.DocumentId, manifest.GetProperty("documentId").GetString());
        Assert.Equal(expectedDocument.Version, manifest.GetProperty("version").GetInt32());
        Assert.Equal(expectedDocument.SourceSystem, manifest.GetProperty("sourceSystem").GetString());
        Assert.Equal(expectedDocument.SourceRecordId, manifest.GetProperty("sourceRecordId").GetString());
        Assert.Equal(expectedDocument.FileName, manifest.GetProperty("fileName").GetString());
        Assert.Equal(expectedDocument.MediaType, manifest.GetProperty("mediaType").GetString());
        Assert.Equal(expectedDocument.Sha256, manifest.GetProperty("sha256").GetString());
        Assert.Equal(expectedDocument.IssuedOn, manifest.GetProperty("issuedOn").GetDateTimeOffset());
        Assert.Equal(operationId, persistedPackage.GetProperty("operationId").GetString());
        Assert.Equal("queued", persistedPackage.GetProperty("processingStatus").GetString());

        var persistedOperation = state.GetProperty("operations")
            .GetProperty(operationId).GetProperty("status");
        Assert.Equal(operationId, persistedOperation.GetProperty("operationId").GetString());
        AssertScope(persistedOperation, expectedPackage);
        Assert.Equal("queued", persistedOperation.GetProperty("status").GetString());

        var persistedDocument = state.GetProperty("documents").EnumerateObject().Single().Value;
        AssertScope(persistedDocument.GetProperty("context"), expectedPackage);
        var metadata = persistedDocument.GetProperty("metadata");
        Assert.Equal(expectedDocument.DocumentId, metadata.GetProperty("documentId").GetString());
        Assert.Equal(expectedDocument.Version, metadata.GetProperty("version").GetInt32());
        Assert.Equal(expectedDocument.SourceSystem, metadata.GetProperty("sourceSystem").GetString());
        Assert.Equal(expectedDocument.SourceRecordId, metadata.GetProperty("sourceRecordId").GetString());
        Assert.Equal(expectedDocument.FileName, metadata.GetProperty("fileName").GetString());
        Assert.Equal(expectedDocument.MediaType, metadata.GetProperty("mediaType").GetString());
        Assert.Equal(expectedDocument.Sha256, metadata.GetProperty("sha256").GetString());
        Assert.Equal(expectedDocument.IssuedOn, metadata.GetProperty("issuedOn").GetDateTimeOffset());
        Assert.False(metadata.TryGetProperty("storageLocator", out _));
    }

    private static void AssertScope(JsonElement value, SubmissionPackage expectedPackage)
    {
        Assert.Equal(expectedPackage.RunId, value.GetProperty("runId").GetString());
        Assert.Equal(expectedPackage.CaseId, value.GetProperty("caseId").GetString());
        Assert.Equal(expectedPackage.AirlineId, value.GetProperty("airlineId").GetString());
        Assert.Equal(expectedPackage.AircraftId, value.GetProperty("aircraftId").GetString());
        Assert.Equal(expectedPackage.LeaseId, value.GetProperty("leaseId").GetString());
    }

    [Fact]
    public async Task AuthorisedReadsRequireCaseAndExactDocumentVersionScopeWithoutMutation()
    {
        using var fixture = new ApiFixture();
        var request = fixture.CreateRequest();

        await using var service = await fixture.StartAsync();
        var accepted = await fixture.PostPackageAsync(service.Client, request);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var before = await File.ReadAllTextAsync(fixture.StatePath);

        using var crossAirline = fixture.CreateClient("OTHER-AIRLINE");
        var crossAirlineCase = await crossAirline.GetAsync("/api/cases/CASE-0001");
        Assert.Equal(HttpStatusCode.NotFound, crossAirlineCase.StatusCode);
        var crossAirlineEvidence = await fixture.GetEvidenceAsync(
            crossAirline, "CASE-0001", "DOC-0001", 1, 1);
        Assert.Equal(HttpStatusCode.NotFound, crossAirlineEvidence.StatusCode);
        Assert.DoesNotContain("page one preview", await crossAirlineEvidence.Content.ReadAsStringAsync());

        var wrongCase = await fixture.GetEvidenceAsync(
            service.Client, "CASE-OTHER", "DOC-0001", 1, 1);
        Assert.Equal(HttpStatusCode.NotFound, wrongCase.StatusCode);
        var wrongVersion = await fixture.GetEvidenceAsync(
            service.Client, "CASE-0001", "DOC-0001", 2, 1);
        Assert.Equal(HttpStatusCode.NotFound, wrongVersion.StatusCode);

        var after = await File.ReadAllTextAsync(fixture.StatePath);
        Assert.Equal(before, after);
        Assert.Equal(0, fixture.Storage.ReadCount);
    }

    [Fact]
    public async Task CaseResponse_ExposesPackageProcessingAndSafeOperationShapeOnly()
    {
        using var fixture = new ApiFixture();
        var request = fixture.CreateRequest();

        await using var service = await fixture.StartAsync();
        var accepted = await fixture.PostPackageAsync(service.Client, request);
        var acceptedJson = await accepted.Content.ReadAsStringAsync();
        var acceptedBody = JsonDocument.Parse(acceptedJson).RootElement;
        fixture.OperationId = acceptedBody.GetProperty("operationId").GetString()!;

        var response = await fixture.GetCaseAsync(service.Client, request.Package.CaseId);
        var body = await response.Content.ReadAsStringAsync();
        var summary = JsonDocument.Parse(body).RootElement;
        var packageState = summary.GetProperty("packageProcessing")[0];

        Assert.Equal(fixture.OperationId, packageState.GetProperty("operationId").GetString());
        Assert.Equal("queued", packageState.GetProperty("status").GetString());
        Assert.DoesNotContain("storageLocator", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("page one preview", body, StringComparison.OrdinalIgnoreCase);

        var operationResponse = await service.Client.GetAsync($"/api/operations/{fixture.OperationId}");
        var operationBody = await operationResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("storageLocator", operationBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("documentContent", operationBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stackTrace", operationBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RestartedService_ExposesPersistedOperationFailureAsSafeCode()
    {
        using var fixture = new ApiFixture();
        var request = fixture.CreateRequest();
        string operationId;

        await using (var service = await fixture.StartAsync())
        {
            var accepted = await fixture.PostPackageAsync(service.Client, request);
            var operation = await accepted.Content.ReadFromJsonAsync<OperationAccepted>();
            operationId = operation!.OperationId;
        }

        var state = JsonNode.Parse(await File.ReadAllTextAsync(fixture.StatePath))!.AsObject();
        var safeError = new JsonObject
        {
            ["safeCode"] = "INVALID_PAYLOAD",
            ["correlationId"] = "CORR-FAILED"
        };
        state["operations"]!.AsObject()[operationId]!["status"]!["status"] = "failed";
        state["operations"]!.AsObject()[operationId]!["status"]!["error"] = safeError.DeepClone();
        state["operations"]!.AsObject()[operationId]!["status"]!["attempts"] =
            new JsonArray
            {
                new JsonObject
                {
                    ["attemptId"] = "ATTEMPT-FAILED",
                    ["attemptNumber"] = 1,
                    ["status"] = "failed",
                    ["error"] = safeError.DeepClone()
                }
            };
        var package = state["packages"]!.AsObject().Single().Value!.AsObject();
        package["processingStatus"] = "failed";
        package["error"] = safeError.DeepClone();
        state["cases"]!.AsObject().Single().Value!["status"] = "blocked";
        await File.WriteAllTextAsync(fixture.StatePath, state.ToJsonString());

        await using var restartedService = await fixture.StartAsync();
        var caseResponse = await fixture.GetCaseAsync(
            restartedService.Client, request.Package.CaseId);
        var summary = await caseResponse.Content.ReadFromJsonAsync<CaseSummary>();
        Assert.NotNull(summary);
        Assert.Equal("failed", summary.PackageProcessing.Single().Status);
        Assert.Equal("INVALID_PAYLOAD",
            summary.PackageProcessing.Single().Error!.SafeCode);

        var operationResponse = await restartedService.Client.GetAsync(
            $"/api/operations/{operationId}");
        var persistedOperation = await operationResponse.Content.ReadFromJsonAsync<OperationStatus>();
        Assert.NotNull(persistedOperation);
        Assert.Equal("INVALID_PAYLOAD", persistedOperation.Error!.SafeCode);
        Assert.Single(persistedOperation.Attempts!);
        Assert.Equal("ATTEMPT-FAILED", persistedOperation.Attempts![0].AttemptId);
        Assert.Equal("failed", persistedOperation.Attempts![0].Status);
        Assert.DoesNotContain("documentContent",
            await operationResponse.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ApiFixture : IDisposable
    {
        private readonly string directory = Path.Combine(
            Path.GetTempPath(), "airlinedemo-tests", Guid.NewGuid().ToString("N"));
        private ApiServer? server;

        public CountingDocumentStorage Storage { get; } = new();
        public string StatePath => Path.Combine(directory, "workflow-state.json");
        public string OperationId { get; set; } = string.Empty;

        public PackageSubmissionRequest CreateRequest() =>
            new(
                new EventEnvelope(
                    "1.0",
                    "EVT-0001",
                    "package.submitted",
                    "RUN-0001",
                    "CASE-0001",
                    "AIRLINE-0001",
                    "MOCK-AC-001",
                    "LEASE-0001",
                    DateTimeOffset.Parse("2026-09-10T13:00:00Z"),
                    DateTimeOffset.Parse("2026-09-10T09:00:00Z"),
                    "CORR-0001",
                    JsonDocument.Parse("{\"packageId\":\"PKG-0001\"}").RootElement),
                new SubmissionPackage(
                    "1.0",
                    "PKG-0001",
                    "RUN-0001",
                    "CASE-0001",
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
                            "maintenance-record.pdf",
                            "application/pdf",
                            new string('a', 64),
                            DateTimeOffset.Parse("2026-09-10T12:00:00Z"))
                    ]));

        public async Task<ApiServer> StartAsync()
        {
            var app = WorkflowApi.Create(directory, Storage);
            server = new ApiServer(app, null!);
            await server.App.StartAsync();
            server.Client = CreateClient();
            return server;
        }

        public HttpClient CreateClient(string airline = "AIRLINE-0001")
        {
            var client = new HttpClient { BaseAddress = server!.BaseAddress };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                $"run=RUN-0001;airline={airline};aircraft=MOCK-AC-001;lease=LEASE-0001");
            return client;
        }

        public async Task<HttpResponseMessage> PostPackageAsync(
            HttpClient client,
            PackageSubmissionRequest request)
        {
            var response = await client.PostAsJsonAsync("/api/packages", request);
            return response;
        }

        public Task<HttpResponseMessage> GetCaseAsync(HttpClient client, string caseId) =>
            client.GetAsync($"/api/cases/{caseId}");

        public Task<HttpResponseMessage> GetEvidenceAsync(
            HttpClient client,
            string caseId,
            string documentId,
            int version,
            int page) =>
            client.GetAsync($"/api/cases/{caseId}/evidence/{documentId}?version={version}&page={page}");

        public void Dispose()
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed class ApiServer(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public WebApplication App { get; } = app;
        public HttpClient Client { get; set; } = client;
        public Uri BaseAddress =>
            new(App.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single());

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.DisposeAsync();
        }
    }

    private sealed class CountingDocumentStorage : IDocumentStorage
    {
        public int ReadCount { get; private set; }

        public Task<string?> ReadPagePreviewAsync(
            string storageLocator,
            int page,
            CancellationToken cancellationToken)
        {
            ReadCount++;
            return Task.FromResult(page == 1 ? "page one preview" : null);
        }
    }
}

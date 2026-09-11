using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AirlineDemo.Api;
using AirlineDemo.Generator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using ApiEventEnvelope = AirlineDemo.Api.EventEnvelope;
using ApiSubmissionPackage = AirlineDemo.Api.SubmissionPackage;

namespace AirlineDemo.Tests;

public sealed class AssessmentPersistenceIntegrationTests
{
    [Fact]
    public async Task BaselinePackageProcessing_PersistsScopedExtractionsAndImmutableEvidenceBases()
    {
        using var fixture = new BaselineFixture();
        var request = fixture.GenerateRequest();

        await using (var server = await fixture.StartAsync())
        {
            var accepted = await server.Client.PostAsJsonAsync("/api/packages", request);
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            var operation = await accepted.Content.ReadFromJsonAsync<OperationAccepted>();
            Assert.NotNull(operation);

            var processed = await server.Client.PostAsync(
                $"/api/operations/{operation!.OperationId}/process",
                null);
            Assert.Equal(HttpStatusCode.Accepted, processed.StatusCode);

            using var state = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.StatePath));
            var extractionRecords = state.RootElement
                .GetProperty("extractionRecords")
                .EnumerateObject()
                .Select(entry => entry.Value)
                .ToArray();
            Assert.Equal(request.Package.Manifest.Count, extractionRecords.Length);
            Assert.All(extractionRecords, record =>
            {
                Assert.Equal("complete", record.GetProperty("processingState").GetString());
                var document = request.Package.Manifest.Single(candidate =>
                    candidate.DocumentId == record.GetProperty("documentId").GetString());
                Assert.Equal(document.Version, record.GetProperty("version").GetInt32());
                Assert.Equal(document.Sha256, record.GetProperty("sha256").GetString());
                Assert.Equal(request.Package.RunId, record.GetProperty("context")
                    .GetProperty("runId").GetString());
                Assert.Equal(request.Package.CaseId, record.GetProperty("context")
                    .GetProperty("caseId").GetString());
                Assert.Equal("deterministic-pdf-parser/1.0",
                    record.GetProperty("parserVersion").GetString());
                Assert.Equal("deterministic-text-extractor/1.0",
                    record.GetProperty("extractorVersion").GetString());
                Assert.Equal(1, record.GetProperty("pageInventory").GetArrayLength());
                Assert.Equal(1, record.GetProperty("pageInventory")[0].GetInt32());
            });

            var basis = state.RootElement.GetProperty("evidenceBases")
                .EnumerateObject()
                .Single()
                .Value;
            Assert.Equal(request.Package.CaseId, basis.GetProperty("context")
                .GetProperty("caseId").GetString());
            Assert.Equal(1, basis.GetProperty("caseRevision").GetInt64());
            Assert.Equal(request.Package.Manifest.Count,
                basis.GetProperty("documentInventory").GetArrayLength());
            Assert.Equal(4, basis.GetProperty("approvedRequirementVersions").GetArrayLength());
            var basisBeforeMutation = basis.GetRawText();

            var secondRequest = fixture.GenerateRequest(
                packageId: "PKG-0002",
                eventId: "EVT-0002",
                correlationId: "CORR-0002");
            var secondAccepted = await server.Client.PostAsJsonAsync(
                "/api/packages", secondRequest);
            Assert.Equal(HttpStatusCode.Accepted, secondAccepted.StatusCode);
            var secondOperation =
                await secondAccepted.Content.ReadFromJsonAsync<OperationAccepted>();
            Assert.NotNull(secondOperation);
            await server.Client.PostAsync(
                $"/api/operations/{secondOperation!.OperationId}/process",
                null);

            using var afterMutation = JsonDocument.Parse(
                await File.ReadAllTextAsync(fixture.StatePath));
            var persistedBases = afterMutation.RootElement.GetProperty("evidenceBases")
                .EnumerateObject()
                .Select(entry => entry.Value)
                .ToArray();
            Assert.Equal(2, persistedBases.Length);
            Assert.Contains(
                persistedBases,
                candidate => candidate.GetProperty("caseRevision").GetInt64() == 1 &&
                    candidate.GetRawText() == basisBeforeMutation);
        }
    }

    [Fact]
    public async Task Submission_RejectsAnOmittedManifestDeclaredFile()
    {
        using var fixture = new BaselineFixture();
        var request = fixture.GenerateRequest();
        File.Delete(Path.Combine(
            fixture.OutputDirectory,
            "application-inputs",
            "package-001",
            request.Package.Manifest[0].FileName));

        await using var server = await fixture.StartAsync();
        var response = await server.Client.PostAsJsonAsync("/api/packages", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(File.Exists(fixture.StatePath));
    }

    [Fact]
    public async Task QueuedOperation_RestartsWithOneReceiptAndCompletesOnce()
    {
        using var fixture = new BaselineFixture();
        var request = fixture.GenerateRequest();
        var server = await fixture.StartAsync();
        var accepted = await server.Client.PostAsJsonAsync("/api/packages", request);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var operation = await accepted.Content.ReadFromJsonAsync<OperationAccepted>();
        Assert.NotNull(operation);
        await server.DisposeAsync();

        await using var restarted = await fixture.StartAsync();
        var processed = await restarted.Client.PostAsync(
            $"/api/operations/{operation!.OperationId}/process",
            null);
        Assert.Equal(HttpStatusCode.Accepted, processed.StatusCode);
        var status = await restarted.Client.GetFromJsonAsync<OperationStatus>(
            $"/api/operations/{operation.OperationId}");
        Assert.NotNull(status);
        Assert.Equal("complete", status!.Status);
        Assert.Single(status.Attempts!);

        using var state = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.StatePath));
        Assert.Single(state.RootElement.GetProperty("receipts").EnumerateObject());
        Assert.Single(state.RootElement.GetProperty("operations").EnumerateObject());
        Assert.Single(state.RootElement.GetProperty("packages").EnumerateObject());
    }

    [Fact]
    public async Task FailedOperation_RestartsWithItsFailedAttemptBeforeRetry()
    {
        using var fixture = new BaselineFixture();
        var request = fixture.GenerateRequest(profile: "processing-failure");
        var storage = new RetryableProcessingFailureStorage(fixture.OutputDirectory);
        var server = await fixture.StartAsync(storage);
        var accepted = await server.Client.PostAsJsonAsync("/api/packages", request);
        var operation = await accepted.Content.ReadFromJsonAsync<OperationAccepted>();
        Assert.NotNull(operation);
        await server.Client.PostAsync(
            $"/api/operations/{operation!.OperationId}/process",
            null);
        await server.DisposeAsync();

        storage.AllowCorruptDocument = true;
        await using var restarted = await fixture.StartAsync(storage);
        var restored = await restarted.Client.GetFromJsonAsync<OperationStatus>(
            $"/api/operations/{operation.OperationId}");
        Assert.NotNull(restored);
        Assert.Equal("failed", restored!.Status);
        var restoredAttempts = restored.Attempts!;
        Assert.Single(restoredAttempts);
        Assert.Equal("failed", restoredAttempts[0].Status);

        var retry = await restarted.Client.PostAsync(
            $"/api/operations/{operation.OperationId}/retry",
            null);
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        await restarted.Client.PostAsync(
            $"/api/operations/{operation.OperationId}/process",
            null);
        var completed = await restarted.Client.GetFromJsonAsync<OperationStatus>(
            $"/api/operations/{operation.OperationId}");
        Assert.NotNull(completed);
        Assert.Equal("complete", completed!.Status);
        Assert.Equal(2, completed.Attempts!.Count);
        Assert.Equal("failed", completed.Attempts[0].Status);
        Assert.Equal("complete", completed.Attempts[1].Status);

        using var state = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.StatePath));
        Assert.Single(state.RootElement.GetProperty("receipts").EnumerateObject());
        Assert.Equal(request.Package.Manifest.Count + 1,
            state.RootElement.GetProperty("extractionAttempts").EnumerateObject().Count());
    }

    [Fact]
    public async Task CorruptDocument_FailsExtractionWithoutBasisAndRetryPreservesAttemptHistory()
    {
        using var fixture = new BaselineFixture();
        var request = fixture.GenerateRequest(profile: "processing-failure");
        var storage = new RetryableProcessingFailureStorage(fixture.OutputDirectory);

        await using var server = await fixture.StartAsync(storage);
        var accepted = await server.Client.PostAsJsonAsync("/api/packages", request);
        var operation = await accepted.Content.ReadFromJsonAsync<OperationAccepted>();
        Assert.NotNull(operation);

        await server.Client.PostAsync(
            $"/api/operations/{operation!.OperationId}/process",
            null);
        var failed = await server.Client.GetFromJsonAsync<OperationStatus>(
            $"/api/operations/{operation.OperationId}");
        Assert.NotNull(failed);
        Assert.Equal("failed", failed!.Status);
        Assert.Equal("INVALID_PAYLOAD", failed.Error!.SafeCode);
        Assert.Single(failed.Attempts!);
        Assert.Equal("failed", failed.Attempts![0].Status);

        using var failedState = JsonDocument.Parse(
            await File.ReadAllTextAsync(fixture.StatePath));
        Assert.Empty(failedState.RootElement.GetProperty("evidenceBases").EnumerateObject());
        var failedRecords = failedState.RootElement.GetProperty("extractionRecords")
            .EnumerateObject()
            .Select(entry => entry.Value)
            .ToArray();
        Assert.Equal(request.Package.Manifest.Count, failedRecords.Length);
        var corruptDocument = request.Package.Manifest.Single(document =>
            document.SourceRecordId == "SOURCE-CORRUPT-0001");
        var failedRecord = failedRecords.Single(record =>
            record.GetProperty("documentId").GetString() == corruptDocument.DocumentId);
        Assert.Equal("failed", failedRecord.GetProperty("processingState").GetString());
        Assert.Equal(corruptDocument.DocumentId, failedRecord.GetProperty("documentId").GetString());
        Assert.Equal(corruptDocument.Version, failedRecord.GetProperty("version").GetInt32());
        Assert.Equal(corruptDocument.Sha256, failedRecord.GetProperty("sha256").GetString());
        Assert.Equal("INVALID_PAYLOAD", failedRecord.GetProperty("error")
            .GetProperty("safeCode").GetString());
        const string readableDocumentId = "DOC-0001";
        var readableRecordBeforeRetry = failedRecords.Single(record =>
            record.GetProperty("documentId").GetString() == readableDocumentId)
            .GetRawText();

        storage.AllowCorruptDocument = true;
        var retry = await server.Client.PostAsync(
            $"/api/operations/{operation.OperationId}/retry",
            null);
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        var retrying = await server.Client.GetFromJsonAsync<OperationStatus>(
            $"/api/operations/{operation.OperationId}");
        Assert.NotNull(retrying);
        Assert.Equal("processing", retrying!.Status);
        Assert.Equal(2, retrying.Attempts!.Count);
        Assert.Equal("failed", retrying.Attempts[0].Status);
        Assert.Equal("processing", retrying.Attempts[1].Status);

        await server.Client.PostAsync(
            $"/api/operations/{operation.OperationId}/process",
            null);
        var completed = await server.Client.GetFromJsonAsync<OperationStatus>(
            $"/api/operations/{operation.OperationId}");
        Assert.NotNull(completed);
        Assert.Equal("complete", completed!.Status);
        Assert.Equal("failed", completed.Attempts![0].Status);
        Assert.Equal("complete", completed.Attempts[1].Status);

        using var finalState = JsonDocument.Parse(
            await File.ReadAllTextAsync(fixture.StatePath));
        Assert.Single(finalState.RootElement.GetProperty("evidenceBases").EnumerateObject());
        var finalRecords = finalState.RootElement.GetProperty("extractionRecords")
            .EnumerateObject()
            .Select(entry => entry.Value)
            .ToArray();
        Assert.Equal(request.Package.Manifest.Count, finalRecords.Length);
        Assert.Equal(
            readableRecordBeforeRetry,
            finalRecords.Single(record =>
                record.GetProperty("documentId").GetString() == readableDocumentId)
                .GetRawText());
        Assert.Equal("complete", finalRecords.Single(record =>
            record.GetProperty("documentId").GetString() == corruptDocument.DocumentId)
            .GetProperty("processingState").GetString());
        var attempts = finalState.RootElement.GetProperty("extractionAttempts")
            .EnumerateObject()
            .Select(entry => entry.Value)
            .ToArray();
        Assert.Equal(request.Package.Manifest.Count + 1, attempts.Length);
        Assert.Contains(attempts, record =>
            record.GetProperty("documentId").GetString() == corruptDocument.DocumentId &&
            record.GetProperty("processingState").GetString() == "failed");
        Assert.Contains(attempts, record =>
            record.GetProperty("documentId").GetString() == corruptDocument.DocumentId &&
            record.GetProperty("processingState").GetString() == "complete");
        Assert.All(attempts, record =>
        {
            Assert.Equal(corruptDocument.DocumentId == record.GetProperty("documentId").GetString()
                ? corruptDocument.Sha256
                : request.Package.Manifest.Single(document =>
                    document.DocumentId == record.GetProperty("documentId").GetString()).Sha256,
                record.GetProperty("sha256").GetString());
            Assert.Equal(request.Package.RunId, record.GetProperty("context")
                .GetProperty("runId").GetString());
            Assert.Equal(request.Package.CaseId, record.GetProperty("context")
                .GetProperty("caseId").GetString());
        });
    }

    [Fact]
    public async Task CorruptDocument_RetryDoesNotMutateAnIndependentRunScope()
    {
        using var fixture = new TwoScopeAssessmentFixture();
        var failedRequest = fixture.Generate(
            "RUN-0001",
            "processing-failure",
            "PKG-FAILURE-0001",
            "EVT-FAILURE-0001",
            "CORR-FAILURE-0001");
        var unaffectedRequest = fixture.Generate(
            "RUN-0002",
            "baseline",
            "PKG-BASELINE-0002",
            "EVT-BASELINE-0002",
            "CORR-BASELINE-0002");

        await using var server = await fixture.StartAsync();
        using var failedClient = fixture.CreateClient(server, failedRequest.Package);
        using var unaffectedClient = fixture.CreateClient(server, unaffectedRequest.Package);

        var failedAccepted = await failedClient.PostAsJsonAsync("/api/packages", failedRequest);
        Assert.Equal(HttpStatusCode.Accepted, failedAccepted.StatusCode);
        var failedOperation = await failedAccepted.Content.ReadFromJsonAsync<OperationAccepted>();
        Assert.NotNull(failedOperation);

        var unaffectedAccepted =
            await unaffectedClient.PostAsJsonAsync("/api/packages", unaffectedRequest);
        Assert.Equal(HttpStatusCode.Accepted, unaffectedAccepted.StatusCode);
        var unaffectedOperation =
            await unaffectedAccepted.Content.ReadFromJsonAsync<OperationAccepted>();
        Assert.NotNull(unaffectedOperation);

        await failedClient.PostAsync(
            $"/api/operations/{failedOperation!.OperationId}/process",
            null);
        await unaffectedClient.PostAsync(
            $"/api/operations/{unaffectedOperation!.OperationId}/process",
            null);

        var unaffectedBeforeRetry = CaptureScopeState(
            fixture.StatePath,
            unaffectedRequest.Package.RunId);

        fixture.AllowCorruptDocument = true;
        var retry = await failedClient.PostAsync(
            $"/api/operations/{failedOperation.OperationId}/retry",
            null);
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        await failedClient.PostAsync(
            $"/api/operations/{failedOperation.OperationId}/process",
            null);

        var failedStatus = await failedClient.GetFromJsonAsync<OperationStatus>(
            $"/api/operations/{failedOperation.OperationId}");
        Assert.NotNull(failedStatus);
        Assert.Equal("complete", failedStatus!.Status);
        Assert.Equal(2, failedStatus.Attempts!.Count);
        Assert.Equal("failed", failedStatus.Attempts[0].Status);
        Assert.Equal("complete", failedStatus.Attempts[1].Status);

        Assert.Equal(
            unaffectedBeforeRetry,
            CaptureScopeState(fixture.StatePath, unaffectedRequest.Package.RunId));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("non-terminal")]
    [InlineData("foreign-scope")]
    [InlineData("version-mismatch")]
    [InlineData("hash-mismatch")]
    public async Task EvidenceBasisCreation_RejectsInvalidExtractionRecord(string defect)
    {
        using var fixture = new BaselineFixture();
        var request = fixture.GenerateRequest();

        string operationId;
        await using (var server = await fixture.StartAsync())
        {
            var accepted = await server.Client.PostAsJsonAsync("/api/packages", request);
            var operation = await accepted.Content.ReadFromJsonAsync<OperationAccepted>();
            Assert.NotNull(operation);
            operationId = operation!.OperationId;

            var state = JsonNode.Parse(await File.ReadAllTextAsync(fixture.StatePath))!.AsObject();
            var document = request.Package.Manifest[0];
            foreach (var candidate in request.Package.Manifest)
            {
                var isDefect = candidate == document;
                if (isDefect && defect == "missing")
                {
                    continue;
                }

                var key = fixture.ExtractionKey(candidate);
                state["extractionRecords"]!.AsObject()[key] = new JsonObject
                {
                    ["context"] = new JsonObject
                    {
                        ["runId"] = "RUN-0001",
                        ["caseId"] = request.Package.CaseId,
                        ["airlineId"] = isDefect && defect == "foreign-scope"
                            ? "AIRLINE-OTHER"
                            : "AIRLINE-0001",
                        ["aircraftId"] = "MOCK-AC-001",
                        ["leaseId"] = "LEASE-0001"
                    },
                    ["documentId"] = candidate.DocumentId,
                    ["version"] = isDefect && defect == "version-mismatch"
                        ? 2
                        : candidate.Version,
                    ["sha256"] = isDefect && defect == "hash-mismatch"
                        ? new string('b', 64)
                        : candidate.Sha256,
                    ["parserVersion"] = "deterministic-pdf-parser/1.0",
                    ["extractorVersion"] = "deterministic-text-extractor/1.0",
                    ["pageInventory"] = new JsonArray(1),
                    ["processingState"] = isDefect && defect == "non-terminal"
                        ? "processing"
                        : "complete"
                };
            }
            await File.WriteAllTextAsync(fixture.StatePath, state.ToJsonString());
        }

        await using var restarted = await fixture.StartAsync();
        var processed = await restarted.Client.PostAsync(
            $"/api/operations/{operationId}/process",
            null);
        Assert.Equal(HttpStatusCode.Accepted, processed.StatusCode);
        var status = await restarted.Client.GetFromJsonAsync<OperationStatus>(
            $"/api/operations/{operationId}");
        Assert.NotNull(status);
        Assert.Equal("failed", status!.Status);

        using var finalState = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.StatePath));
        Assert.Empty(finalState.RootElement.GetProperty("evidenceBases").EnumerateObject());
    }

    private sealed class BaselineFixture : IDisposable
    {
        private readonly string directory = Path.Combine(
            Path.GetTempPath(), "airlinedemo-assessment-tests", Guid.NewGuid().ToString("N"));
        private GeneratedFixture? generated;
        private ApiServer? server;

        public string OutputDirectory => Path.Combine(directory, "fixture");
        public string StatePath => Path.Combine(directory, "state", "workflow-state.json");

        public PackageSubmissionRequest GenerateRequest(
            string packageId = "PKG-0001",
            string eventId = "EVT-0001",
            string correlationId = "CORR-0001",
            string profile = "baseline")
        {
            generated = FixtureGenerator.Generate(
                new GeneratorConfiguration(
                    42,
                    "assessment-fixture-1",
                    new DateOnly(2026, 9, 10),
                    "RUN-0001",
                    profile,
                    OutputDirectory,
                    new Dictionary<string, string>
                    {
                        ["dotnet"] = "10.0.401",
                        ["generator"] = "1.0.0"
                    }),
                profile == "baseline"
                    ? "template-assessment-1"
                    : $"template-{profile}-1");
            var sourcePackage = generated.ContractPackage.SubmissionPackages.Single();
            var sourceEvent = generated.ContractPackage.Events.Single();
            using var payload = JsonDocument.Parse($$"""{"packageId":"{{packageId}}"}""");
            var package = new ApiSubmissionPackage(
                sourcePackage.SchemaVersion,
                packageId,
                sourcePackage.RunId,
                sourcePackage.CaseId,
                sourcePackage.AirlineId,
                sourcePackage.AircraftId,
                sourcePackage.LeaseId,
                sourcePackage.SubmittedAt,
                sourcePackage.ScenarioEffectiveAt,
                sourcePackage.Manifest.Select(document => new DocumentMetadata(
                    document.DocumentId,
                    document.Version,
                    document.SourceSystem,
                    document.SourceRecordId,
                    document.FileName,
                    document.MediaType,
                    document.Sha256,
                    document.IssuedOn)).ToArray());
            return new PackageSubmissionRequest(
                new ApiEventEnvelope(
                    sourceEvent.SchemaVersion,
                    eventId,
                    sourceEvent.Type,
                    sourceEvent.RunId,
                    sourceEvent.CaseId,
                    sourceEvent.AirlineId,
                    sourceEvent.AircraftId,
                    sourceEvent.LeaseId,
                    sourceEvent.OccurredAt,
                    sourceEvent.ScenarioEffectiveAt,
                    correlationId,
                    payload.RootElement.Clone()),
                package);
        }

        public string ExtractionKey(DocumentMetadata document) =>
            $"RUN-0001:AIRLINE-0001:MOCK-AC-001:LEASE-0001:CASE-RUN-0001:" +
            $"{document.DocumentId}:v{document.Version}";

        public async Task<ApiServer> StartAsync(IDocumentStorage? storage = null)
        {
            var app = WorkflowApi.Create(
                Path.Combine(directory, "state"),
                storage ?? new FileDocumentStorage(OutputDirectory));
            server = new ApiServer(app);
            await server.App.StartAsync();
            server.Client = new HttpClient { BaseAddress = server.BaseAddress };
            server.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                "run=RUN-0001;airline=AIRLINE-0001;aircraft=MOCK-AC-001;lease=LEASE-0001");
            return server;
        }

        public void Dispose()
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed class RetryableProcessingFailureStorage(string rootDirectory) :
        IDocumentStorage,
        IManifestDocumentStorage,
        IPageInventoryStorage,
        IApprovedRequirementStorage
    {
        private readonly FileDocumentStorage inner = new(rootDirectory);

        public bool AllowCorruptDocument { get; set; }

        public Task<string?> ReadPagePreviewAsync(
            string storageLocator,
            int page,
            CancellationToken cancellationToken) =>
            inner.ReadPagePreviewAsync(storageLocator, page, cancellationToken);

        public Task<string?> ValidateManifestAsync(
            AirlineDemo.Api.CaseContext context,
            IReadOnlyList<DocumentMetadata> manifest,
            CancellationToken cancellationToken) =>
            inner.ValidateManifestAsync(context, manifest, cancellationToken);

        public Task<IReadOnlyList<int>> GetPageInventoryAsync(
            AirlineDemo.Api.CaseContext context,
            DocumentMetadata document,
            CancellationToken cancellationToken) =>
            document.SourceRecordId == "SOURCE-CORRUPT-0001" && AllowCorruptDocument
                ? Task.FromResult<IReadOnlyList<int>>([1])
                : inner.GetPageInventoryAsync(context, document, cancellationToken);

        public Task<IReadOnlyList<ApprovedRequirementVersion>> GetApprovedRequirementVersionsAsync(
            AirlineDemo.Api.CaseContext context,
            CancellationToken cancellationToken) =>
            inner.GetApprovedRequirementVersionsAsync(context, cancellationToken);
    }

    private sealed class TwoScopeAssessmentFixture : IDisposable
    {
        private readonly string directory = Path.Combine(
            Path.GetTempPath(), "airlinedemo-two-scope-assessment-tests",
            Guid.NewGuid().ToString("N"));
        private ApiServer? server;

        public string StatePath => Path.Combine(directory, "state", "workflow-state.json");
        public bool AllowCorruptDocument { get; set; }

        public PackageSubmissionRequest Generate(
            string runId,
            string profile,
            string packageId,
            string eventId,
            string correlationId)
        {
            var outputDirectory = Path.Combine(directory, runId);
            var fixture = FixtureGenerator.Generate(
                new GeneratorConfiguration(
                    42,
                    $"assessment-{runId}",
                    new DateOnly(2026, 9, 10),
                    runId,
                    profile,
                    outputDirectory,
                    new Dictionary<string, string>
                    {
                        ["dotnet"] = "10.0.401",
                        ["generator"] = "1.0.0"
                    }),
                $"template-{profile}-{runId}");
            return ToApiRequest(fixture.ContractPackage, packageId, eventId, correlationId);
        }

        public async Task<ApiServer> StartAsync()
        {
            var failureStorage = new RetryableProcessingFailureStorage(
                Path.Combine(directory, "RUN-0001"));
            var storage = new ScopedDocumentStorage(
                new Dictionary<string, IDocumentStorage>(StringComparer.Ordinal)
                {
                    ["RUN-0001"] = failureStorage,
                    ["RUN-0002"] = new FileDocumentStorage(
                        Path.Combine(directory, "RUN-0002"))
                });
            storage.CorruptDocumentAllowed = () =>
            {
                failureStorage.AllowCorruptDocument = AllowCorruptDocument;
                return AllowCorruptDocument;
            };
            var app = WorkflowApi.Create(Path.Combine(directory, "state"), storage);
            server = new ApiServer(app);
            await server.App.StartAsync();
            server.Client = new HttpClient { BaseAddress = server.BaseAddress };
            return server;
        }

        public HttpClient CreateClient(ApiServer apiServer, ApiSubmissionPackage package)
        {
            var client = new HttpClient { BaseAddress = apiServer.BaseAddress };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                $"run={package.RunId};airline={package.AirlineId};" +
                $"aircraft={package.AircraftId};lease={package.LeaseId}");
            return client;
        }

        public void Dispose()
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        private static PackageSubmissionRequest ToApiRequest(
            GeneratorContractPackage package,
            string packageId,
            string eventId,
            string correlationId)
        {
            var sourceEvent = package.Events.Single();
            var sourcePackage = package.SubmissionPackages.Single();
            var documents = sourcePackage.Manifest
                .Select(document => new DocumentMetadata(
                    document.DocumentId,
                    document.Version,
                    document.SourceSystem,
                    document.SourceRecordId,
                    document.FileName,
                    document.MediaType,
                    document.Sha256,
                    document.IssuedOn))
                .ToArray();
            using var payload = JsonDocument.Parse($$"""{"packageId":"{{packageId}}"}""");
            return new PackageSubmissionRequest(
                new ApiEventEnvelope(
                    sourceEvent.SchemaVersion,
                    eventId,
                    sourceEvent.Type,
                    sourceEvent.RunId,
                    sourceEvent.CaseId,
                    sourceEvent.AirlineId,
                    sourceEvent.AircraftId,
                    sourceEvent.LeaseId,
                    sourceEvent.OccurredAt,
                    sourceEvent.ScenarioEffectiveAt,
                    correlationId,
                    payload.RootElement.Clone()),
                new ApiSubmissionPackage(
                    sourcePackage.SchemaVersion,
                    packageId,
                    sourcePackage.RunId,
                    sourcePackage.CaseId,
                    sourcePackage.AirlineId,
                    sourcePackage.AircraftId,
                    sourcePackage.LeaseId,
                    sourcePackage.SubmittedAt,
                    sourcePackage.ScenarioEffectiveAt,
                    documents));
        }
    }

    private sealed class ScopedDocumentStorage(
        IReadOnlyDictionary<string, IDocumentStorage> storages) :
        IDocumentStorage,
        IManifestDocumentStorage,
        ISelectedManifestStorage,
        IPageInventoryStorage,
        IApprovedRequirementStorage
    {
        public Func<bool>? CorruptDocumentAllowed { get; set; }

        public Task<string?> ReadPagePreviewAsync(
            string storageLocator,
            int page,
            CancellationToken cancellationToken) =>
            Resolve(storageLocator.Split('/', 2)[0]).ReadPagePreviewAsync(
                storageLocator, page, cancellationToken);

        public Task<string?> ValidateManifestAsync(
            AirlineDemo.Api.CaseContext context,
            IReadOnlyList<DocumentMetadata> manifest,
            CancellationToken cancellationToken) =>
            ((IManifestDocumentStorage)Resolve(context)).ValidateManifestAsync(
                context, manifest, cancellationToken);

        public Task<string?> GetSelectedManifestSha256Async(
            AirlineDemo.Api.CaseContext context,
            CancellationToken cancellationToken) =>
            Resolve(context) is ISelectedManifestStorage selectedStorage
                ? selectedStorage.GetSelectedManifestSha256Async(context, cancellationToken)
                : Task.FromResult<string?>(null);

        public Task<IReadOnlyList<int>> GetPageInventoryAsync(
            AirlineDemo.Api.CaseContext context,
            DocumentMetadata document,
            CancellationToken cancellationToken)
        {
            if (context.RunId == "RUN-0001" &&
                document.SourceRecordId == "SOURCE-CORRUPT-0001" &&
                CorruptDocumentAllowed?.Invoke() != true)
            {
                return Task.FromResult<IReadOnlyList<int>>([]);
            }

            return ((IPageInventoryStorage)Resolve(context)).GetPageInventoryAsync(
                context, document, cancellationToken);
        }

        public Task<IReadOnlyList<ApprovedRequirementVersion>> GetApprovedRequirementVersionsAsync(
            AirlineDemo.Api.CaseContext context,
            CancellationToken cancellationToken) =>
            ((IApprovedRequirementStorage)Resolve(context))
                .GetApprovedRequirementVersionsAsync(context, cancellationToken);

        private IDocumentStorage Resolve(string runId) =>
            storages.TryGetValue(runId, out var storage)
                ? storage
                : throw new InvalidOperationException($"No storage configured for {runId}.");

        private IDocumentStorage Resolve(AirlineDemo.Api.CaseContext context) =>
            Resolve(context.RunId);
    }

    private static Dictionary<string, string> CaptureScopeState(
        string statePath,
        string runId)
    {
        using var state = JsonDocument.Parse(File.ReadAllText(statePath));
        var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in new[]
        {
            "cases",
            "packages",
            "operations",
            "documents",
            "extractionRecords",
            "extractionAttempts",
            "evidenceBases",
            "receipts"
        })
        {
            foreach (var entry in state.RootElement.GetProperty(property).EnumerateObject())
            {
                var value = entry.Value;
                var entryRunId = property switch
                {
                    "operations" => value.GetProperty("status").GetProperty("runId").GetString(),
                    "receipts" => value.GetProperty("runId").GetString(),
                    _ => value.GetProperty("context").GetProperty("runId").GetString()
                };
                if (entryRunId == runId)
                {
                    snapshot[$"{property}:{entry.Name}"] = value.GetRawText();
                }
            }
        }

        return snapshot;
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

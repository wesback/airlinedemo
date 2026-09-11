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
            string correlationId = "CORR-0001")
        {
            generated = FixtureGenerator.Generate(
                new GeneratorConfiguration(
                    42,
                    "assessment-fixture-1",
                    new DateOnly(2026, 9, 10),
                    "RUN-0001",
                    "baseline",
                    OutputDirectory,
                    new Dictionary<string, string>
                    {
                        ["dotnet"] = "10.0.401",
                        ["generator"] = "1.0.0"
                    }),
                "template-assessment-1");
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

        public async Task<ApiServer> StartAsync()
        {
            var app = WorkflowApi.Create(
                Path.Combine(directory, "state"),
                new FileDocumentStorage(OutputDirectory));
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

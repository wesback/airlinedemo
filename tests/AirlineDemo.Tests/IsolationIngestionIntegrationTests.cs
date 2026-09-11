using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AirlineDemo.Api;
using AirlineDemo.Generator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using ApiEventEnvelope = AirlineDemo.Api.EventEnvelope;
using ApiSubmissionPackage = AirlineDemo.Api.SubmissionPackage;

namespace AirlineDemo.Tests;

public sealed class IsolationIngestionIntegrationTests
{
    [Fact]
    public async Task IsolationLoader_RejectsAnUndeclaredFile()
    {
        using var fixture = new IsolationFixture();
        var package = fixture.Generate();
        await File.WriteAllTextAsync(
            Path.Combine(fixture.PackageDirectory, "undeclared.pdf"),
            "not in selected manifest");

        await using var server = await fixture.StartAsync();
        var response = await fixture.PostAsync(server.Client, package);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(
            "not in selected manifest",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.StatePath));
    }

    [Fact]
    public async Task IsolationLoader_RejectsASelectedFileHashMismatch()
    {
        using var fixture = new IsolationFixture();
        var package = fixture.Generate();
        await File.AppendAllTextAsync(
            Path.Combine(fixture.PackageDirectory, package.Package.Manifest[0].FileName),
            "tampered");

        await using var server = await fixture.StartAsync();
        var response = await fixture.PostAsync(server.Client, package);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(File.Exists(fixture.StatePath));
    }

    [Fact]
    public async Task IsolationLoader_RejectsARecursiveDirectoryLoadAttempt()
    {
        using var fixture = new IsolationFixture();
        var package = fixture.Generate();
        var nestedDirectory = Path.Combine(fixture.PackageDirectory, "nested");
        Directory.CreateDirectory(nestedDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(nestedDirectory, "foreign.pdf"),
            "recursive load must not be accepted");

        await using var server = await fixture.StartAsync();
        var response = await fixture.PostAsync(server.Client, package);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(File.Exists(fixture.StatePath));
    }

    [Fact]
    public async Task IsolationIngestion_RejectsForeignPackageRetrievalAndSubmissionWithoutMutation()
    {
        using var fixture = new IsolationFixture();
        var package = fixture.Generate();
        await using var server = await fixture.StartAsync();

        var accepted = await fixture.PostAsync(server.Client, package);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var before = await File.ReadAllTextAsync(fixture.StatePath);

        var foreignCase = await server.Client.GetAsync("/api/cases/CASE-0002");
        Assert.Equal(HttpStatusCode.NotFound, foreignCase.StatusCode);
        var foreignEvidence = await server.Client.GetAsync(
            "/api/cases/CASE-0002/evidence/DOC-ISOLATION-0001?version=1&page=1");
        Assert.Equal(HttpStatusCode.NotFound, foreignEvidence.StatusCode);

        var foreignSubmission = fixture.CreateForeignPackage();
        var rejected = await fixture.PostAsync(server.Client, foreignSubmission);
        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        var rejectionBody = await rejected.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Meridian Skies", rejectionBody, StringComparison.Ordinal);
        Assert.DoesNotContain("cross-scope", rejectionBody, StringComparison.OrdinalIgnoreCase);

        var associationAttempt = await fixture.PostAsync(
            server.Client,
            fixture.CreateForeignPackage("CASE-RUN-0001"));
        Assert.Equal(HttpStatusCode.Forbidden, associationAttempt.StatusCode);
        Assert.DoesNotContain(
            "Meridian Skies",
            await associationAttempt.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllTextAsync(fixture.StatePath));
    }

    [Fact]
    public async Task IsolationIngestion_PersistsOnlyTheSelectedRunAndCaseScope()
    {
        using var fixture = new IsolationFixture();
        var package = fixture.Generate();
        await using var server = await fixture.StartAsync();

        var accepted = await fixture.PostAsync(server.Client, package);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);

        using var state = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.StatePath));
        Assert.Single(state.RootElement.GetProperty("cases").EnumerateObject());
        Assert.Single(state.RootElement.GetProperty("packages").EnumerateObject());
        Assert.Equal(10, state.RootElement.GetProperty("documents").EnumerateObject().Count());
        Assert.All(
            state.RootElement.GetProperty("cases").EnumerateObject(),
            value => AssertSelectedScope(value.Value.GetProperty("context")));
        Assert.All(
            state.RootElement.GetProperty("packages").EnumerateObject(),
            value => AssertSelectedScope(value.Value.GetProperty("context")));
        Assert.All(
            state.RootElement.GetProperty("documents").EnumerateObject(),
            value => AssertSelectedScope(value.Value.GetProperty("context")));
        Assert.DoesNotContain(
            "AIRLINE-0002",
            await File.ReadAllTextAsync(fixture.StatePath),
            StringComparison.Ordinal);
    }

    private static void AssertSelectedScope(JsonElement context)
    {
        Assert.Equal("RUN-0001", context.GetProperty("runId").GetString());
        Assert.Equal("CASE-RUN-0001", context.GetProperty("caseId").GetString());
        Assert.Equal("AIRLINE-0001", context.GetProperty("airlineId").GetString());
        Assert.Equal("MOCK-AC-001", context.GetProperty("aircraftId").GetString());
        Assert.Equal("LEASE-0001", context.GetProperty("leaseId").GetString());
    }

    private sealed class IsolationFixture : IDisposable
    {
        private readonly string directory = Path.Combine(
            Path.GetTempPath(), "airlinedemo-isolation-ingestion-tests", Guid.NewGuid().ToString("N"));
        private ApiServer? server;
        private GeneratedFixture? generated;

        public string OutputDirectory => Path.Combine(directory, "fixture");
        public string PackageDirectory => Path.Combine(OutputDirectory, "application-inputs", "package-001");
        public string StatePath => Path.Combine(directory, "state", "workflow-state.json");

        public PackageSubmissionRequest Generate()
        {
            generated = FixtureGenerator.Generate(
                new GeneratorConfiguration(
                    42,
                    "isolation-fixture-1",
                    new DateOnly(2026, 9, 10),
                    "RUN-0001",
                    "isolation",
                    OutputDirectory,
                    new Dictionary<string, string>
                    {
                        ["dotnet"] = "10.0.401",
                        ["generator"] = "1.0.0"
                    }),
                "template-isolation-1");
            return ToApiRequest(generated.ContractPackage);
        }

        public async Task<ApiServer> StartAsync()
        {
            var app = WorkflowApi.Create(
                Path.Combine(directory, "state"),
                new FileDocumentStorage(OutputDirectory));
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

        public PackageSubmissionRequest CreateForeignPackage(string caseId = "CASE-0002")
        {
            var foreignArtifact = generated!.Receipt.ArtifactEvidence!
                .Single(artifact => artifact.Scope.AirlineId == "AIRLINE-0002" &&
                    artifact.RelativePath.EndsWith(".pdf", StringComparison.Ordinal));
            using var payload = JsonDocument.Parse(
                """{"packageId":"PKG-ISOLATION-0001"}""");
            var timestamp = DateTimeOffset.Parse("2026-09-09T00:00:00Z");
            var document = new DocumentMetadata(
                "DOC-ISOLATION-0001",
                1,
                "fixture-generator",
                "ISOLATION-0001",
                Path.GetFileName(foreignArtifact.RelativePath),
                "application/pdf",
                foreignArtifact.Sha256,
                timestamp);
            var package = new ApiSubmissionPackage(
                "1.0",
                "PKG-ISOLATION-0001",
                "RUN-0001",
                caseId,
                "AIRLINE-0002",
                "MOCK-AC-002",
                "LEASE-0002",
                timestamp,
                timestamp,
                [document]);
            return new PackageSubmissionRequest(
                new ApiEventEnvelope(
                    "1.0",
                    "EVT-ISOLATION-0001",
                    "package.submitted",
                    package.RunId,
                    package.CaseId,
                    package.AirlineId,
                    package.AircraftId,
                    package.LeaseId,
                    timestamp,
                    timestamp,
                    "CORR-ISOLATION-0001",
                    payload.RootElement.Clone()),
                package);
        }

        public void Dispose()
        {
            server?.Client.Dispose();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        private static PackageSubmissionRequest ToApiRequest(
            GeneratorContractPackage package)
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
            return new PackageSubmissionRequest(
                new ApiEventEnvelope(
                    sourceEvent.SchemaVersion,
                    sourceEvent.EventId,
                    sourceEvent.Type,
                    sourceEvent.RunId,
                    sourceEvent.CaseId,
                    sourceEvent.AirlineId,
                    sourceEvent.AircraftId,
                    sourceEvent.LeaseId,
                    sourceEvent.OccurredAt,
                    sourceEvent.ScenarioEffectiveAt,
                    sourceEvent.CorrelationId,
                    sourceEvent.Payload),
                new ApiSubmissionPackage(
                    sourcePackage.SchemaVersion,
                    sourcePackage.PackageId,
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

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AirlineDemo.Api.Admission;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Evidence;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.SourceSystems;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AirlineDemo.Api.Tests;

public sealed class ArrivalHandoffTests : IDisposable
{
    private const string CaseId = "case-001";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arrival-handoff-" + Guid.NewGuid().ToString("N"));
    private string CasePath => Path.Combine(_directory, "cases.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    public class IntakeModeRaceStore : DispatchProxy
    {
        public ICaseStateStore Inner { get; set; } = null!;
        public bool ModeChanged { get; private set; }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (!ModeChanged && method?.Name == nameof(ICaseStateStore.Commit)
                && args?[2] is CaseCommit { Actor: IntakeJourneyHandover.IntakeActor })
            {
                var scope = (CaseScope)args[0]!;
                var state = Inner.Read(scope);
                var result = new SourceModeStore(Inner, TimeProvider.System).Set(
                    scope, "presenter", "maintenance-unavailable-during-intake", state.Revision, "maintenance", "unavailable");
                if (result.Outcome != SourceModeOutcome.Recorded)
                    throw new InvalidOperationException("The concurrent source-mode update was not recorded.");
                ModeChanged = true;
            }

            try
            {
                return method!.Invoke(Inner, args);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }
    }

    [Fact]
    public async Task IncompleteArrivalReachesNoParserStoreOrModel()
    {
        var model = new CountingChatClient();
        using var factory = CreateFactory(arrivalJourney: true, model);
        using var client = Session(factory);
        var store = factory.Services.GetRequiredService<ICaseStateStore>();
        var scope = store.AuthorizeCase(CaseId, "presenter");
        var original = store.Read(scope);

        for (var attempt = 0; attempt < WorkloadAdmissionLimits.PackageAttemptsPerWindow + 1; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/cases/{CaseId}/packages")
            {
                Content = new StringContent("This is not multipart form data."),
            };
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal("arrival_sources_missing", body.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal(SourceModes.Sources,
                body.RootElement.GetProperty("error").GetProperty("missingSources").EnumerateArray()
                    .Select(item => item.GetString()).ToArray());
        }

        Assert.Equal(original, store.Read(scope));
        Assert.Empty(factory.Services.GetRequiredService<EvidencePackageStore>().ListForCase(CaseId));
        Assert.Empty(factory.Services.GetRequiredService<WorkloadAdmissionLimits>().Events);
        Assert.Equal(0, model.Calls);

        var arrival = factory.Services.GetRequiredService<ArrivalJourneyService>();
        Assert.Equal(ArrivalStartOutcome.Started,
            arrival.Start(scope, "presenter", new ArrivalCommand("start-before-intake", original.Revision)).Outcome);
        var started = store.Read(scope);
        var basis = EvidenceIntake.CreateSourceBasis(AllSourcePins());
        store.Commit(scope, started.Revision, new CaseCommit(
            CaseStateStore.CreateBasisVersion(1, basis.Content, string.Empty), null,
            "source-fixture", "source-basis.gathered", "Seed complete source pins for gate test."));
        var completePins = store.Read(scope);
        Assert.Empty(arrival.MissingSources(completePins));

        using var notCompleted = await client.PostAsync($"/api/cases/{CaseId}/packages",
            new StringContent("This is not multipart form data."), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, notCompleted.StatusCode);
        using var notCompletedBody = JsonDocument.Parse(await notCompleted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("arrival_not_completed", notCompletedBody.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(completePins, store.Read(scope));
        Assert.Empty(factory.Services.GetRequiredService<EvidencePackageStore>().ListForCase(CaseId));
        Assert.Empty(factory.Services.GetRequiredService<WorkloadAdmissionLimits>().Events);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task CompletedArrivalPreservesPinsDuringIntake()
    {
        var model = new CountingChatClient();
        using var factory = CreateFactory(arrivalJourney: true, model, raceModeChangeDuringIntake: true);
        var store = factory.Services.GetRequiredService<ICaseStateStore>();
        var scope = store.AuthorizeCase(CaseId, "presenter");
        var arrival = factory.Services.GetRequiredService<ArrivalJourneyService>();
        var state = store.Read(scope);
        Assert.Equal(ArrivalStartOutcome.Started,
            arrival.Start(scope, "presenter", new ArrivalCommand("start-intake", state.Revision)).Outcome);

        state = store.Read(scope);
        var sourceBasis = EvidenceIntake.CreateSourceBasis(AllSourcePins());
        store.Commit(scope, state.Revision, new CaseCommit(
            CaseStateStore.CreateBasisVersion(1, sourceBasis.Content, string.Empty), null,
            "source-fixture", "source-basis.gathered", "Seed fixture pins for completed intake test."));
        state = store.Read(scope);
        var completion = arrival.Complete(scope, "presenter", new ArrivalCommand("complete-intake", state.Revision));
        Assert.Equal(ArrivalCompletionOutcome.Completed, completion.Outcome);
        var before = store.Read(scope);
        var frozenBasis = Assert.Single(before.Basis);
        var originalAudit = before.Audit;

        using var client = Session(factory);
        using var response = await PostPackage(client, "valid-text.pdf", ReadFixture("valid-text.pdf"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(0, model.Calls);
        Assert.True(((IntakeModeRaceStore)(object)store).ModeChanged);

        var after = store.Read(scope);
        Assert.Equal("ready", arrival.Status(after).State);
        Assert.Empty(arrival.Status(after).MissingSources);
        Assert.Equal(2, after.Basis.Count);
        Assert.Equal(frozenBasis, after.Basis[0]);
        Assert.Equal(frozenBasis.Content, after.Basis[1].Content);
        Assert.Equal(CaseStateStore.CreateBasisVersion(2, frozenBasis.Content, frozenBasis.BasisDigest).BasisDigest,
            after.Basis[1].BasisDigest);
        Assert.Equal(originalAudit, after.Audit.Take(originalAudit.Count));
        Assert.Equal(SourceModes.Sources.Length,
            JsonDocument.Parse(after.Basis[1].Content).RootElement.GetProperty("pins").GetArrayLength());

        var packageEvent = Assert.Single(after.JourneyEvents!.Where(item => item.EventType == "package-received"));
        Assert.Contains(packageEvent.Witnesses, witness => witness.DocumentId == "valid-text.pdf" && witness.DocumentVersion == 1);
        Assert.Contains(factory.Services.GetRequiredService<EvidenceBasisStore>().History($"{CaseId}/valid-text.pdf"),
            version => !string.IsNullOrWhiteSpace(version.Text));
        Assert.Contains(after.JourneyEvents!, item => item.EventType == "investigation-escalated");
        Assert.Single(after.WorkItems!);
        var latestSources = await factory.Services.GetRequiredService<SourceSummaryService>().BuildAsync(
            CaseId, "technical-reviewer", after,
            factory.Services.GetRequiredService<SourceModeStore>().ModesFor(after), TestContext.Current.CancellationToken);
        Assert.Equal(SourceModes.Sources.Length, latestSources.Sources!.Count);
        Assert.Equal("unavailable", latestSources.Sources.Single(source => source.SourceId == "maintenance").Mode);
        Assert.True(latestSources.Sources.Single(source => source.SourceId == "maintenance").PinnedInBasis);
    }

    [Fact]
    public async Task LegacyPackagesKeepExistingBehavior()
    {
        var model = new CountingChatClient();
        using var factory = CreateFactory(arrivalJourney: false, model);
        using var client = Session(factory);
        using var response = await PostPackage(client, "valid-text.pdf", ReadFixture("valid-text.pdf"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(0, model.Calls);

        var store = factory.Services.GetRequiredService<ICaseStateStore>();
        var state = store.Read(store.AuthorizeCase(CaseId, "presenter"));
        Assert.Single(state.Basis);
        Assert.Equal($"Records package {(await response.Content.ReadFromJsonAsync<EvidencePackageRecord>(TestContext.Current.CancellationToken))!.PackageId}",
            state.Basis[0].Content);
        Assert.DoesNotContain(state.Audit, item => item.Action == ArrivalJourneyService.EnrollmentAction);
        Assert.Single(state.JourneyEvents!, item => item.EventType == "package-received");
        Assert.Contains(state.JourneyEvents!, item => item.EventType == "investigation-escalated");
    }

    private WebApplicationFactory<Program> CreateFactory(
        bool arrivalJourney, IChatClient model, bool raceModeChangeDuringIntake = false)
    {
        Directory.CreateDirectory(_directory);
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Demo:ArrivalJourney"] = arrivalJourney ? "true" : "false",
                    ["Persistence:CaseStateProvider"] = "File",
                    ["CaseState:Path"] = CasePath,
                }));
            builder.ConfigureTestServices(services =>
            {
                if (raceModeChangeDuringIntake)
                {
                    services.RemoveAll<ICaseStateStore>();
                    var proxy = DispatchProxy.Create<ICaseStateStore, IntakeModeRaceStore>();
                    ((IntakeModeRaceStore)(object)proxy).Inner = new CaseStateStore(CasePath);
                    services.AddSingleton(proxy);
                }
                services.AddSingleton(model);
                services.AddSingleton(FakeApprovedManifest.Approved());
                services.AddSingleton(new IsolatedPdfParserOptions
                {
                    WorkerFileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
                    WorkerArguments =
                    [
                        typeof(IsolatedPdfParser).Assembly.Location,
                        AirlineDemo.Api.Evidence.PdfWorker.PdfWorkerEntry.Switch,
                    ],
                });
            });
        });
    }

    private static HttpClient Session(WebApplicationFactory<Program> factory)
    {
        var session = factory.Services.GetRequiredService<DemoSessionStore>().Create(
            new DemoUser("presenter", "Presenter", "presenter", [CaseId]));
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"{DemoAuthEndpoints.CookieName}={session.Id}");
        client.DefaultRequestHeaders.Add(DemoAuthEndpoints.CsrfHeaderName, session.CsrfToken);
        return client;
    }

    private static async Task<HttpResponseMessage> PostPackage(HttpClient client, string fileName, byte[] bytes)
    {
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(part, EvidenceEndpoints.FormFieldName, fileName);
        return await client.PostAsync($"/api/cases/{CaseId}/packages", form, TestContext.Current.CancellationToken);
    }

    private static byte[] ReadFixture(string fileName)
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null && !Directory.Exists(Path.Combine(directory, "Fixtures", "PdfCorpus")))
            directory = Path.GetDirectoryName(directory);
        return File.ReadAllBytes(Path.Combine(directory!, "Fixtures", "PdfCorpus", fileName));
    }

    private static IReadOnlyList<SourceBasisPin> AllSourcePins() => SourceModes.Sources.Select(sourceId =>
    {
        var recordId = sourceId + "-record-1";
        var content = JsonSerializer.Serialize(new
        {
            asOf = "2026-11-01",
            caseId = CaseId,
            kind = sourceId,
            payload = new { fields = new Dictionary<string, string> { ["serialNumber"] = "SYN-" + sourceId } },
            recordId,
            recordVersion = 1,
            sourceId,
            stale = false,
            synthetic = true,
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { DictionaryKeyPolicy = null });
        return new SourceBasisPin(sourceId, recordId, 1, content, Digest(content));
    }).ToArray();

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

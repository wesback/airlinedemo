using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Net.Http.Json;
using System.Text.Json;
using AirlineDemo.Api.Investigation;
using AirlineDemo.Api.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AirlineDemo.Api.Tests;

public sealed class EvidenceInvestigationTests : IDisposable
{
    private const string Passcode = "test-passcode";
    private const string Case = "case-001";

    private readonly string _statePath = Path.Combine(Path.GetTempPath(), $"case-state-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_statePath))
        {
            File.Delete(_statePath);
        }
    }

    // Fails AppendTrace on demand and forwards every other call to the real file-backed store.
    public class AppendFailingStore : DispatchProxy
    {
        public ICaseStateStore Inner { get; set; } = null!;

        public bool FailAppends { get; set; } = true;

        public string? AttemptedRunId { get; private set; }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name == nameof(ICaseStateStore.AppendTrace))
            {
                AttemptedRunId = ((InvestigationTrace)args![1]!).RunId;
                if (FailAppends) throw new IOException("trace append failed");
            }

            try
            {
                return method!.Invoke(Inner, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                throw ex.InnerException;
            }
        }
    }

    private WebApplicationFactory<Program> CreateFactory(IChatClient client, ICaseStateStore? store = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Demo:Passcode"] = Passcode,
                    ["CaseState:Path"] = _statePath,
                }));
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(client);
                services.AddSingleton(FakeApprovedManifest.Approved());
                if (store is not null) services.AddSingleton(store);
                // The test host runs as root on Linux, so the default parser cannot isolate its worker; point it at this test assembly.
                services.AddSingleton(new AirlineDemo.Api.Evidence.IsolatedPdfParserOptions
                {
                    WorkerFileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
                    WorkerArguments = new[] { typeof(AirlineDemo.Api.Evidence.IsolatedPdfParser).Assembly.Location, AirlineDemo.Api.Evidence.PdfWorker.PdfWorkerEntry.Switch },
                });
            });
        });

    private static byte[] ReadFixture(string name)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "Fixtures", "PdfCorpus")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return File.ReadAllBytes(Path.Combine(dir!, "Fixtures", "PdfCorpus", name));
    }

    private static async Task<(string Cookie, string Csrf)> SignInAsync(HttpClient client, string userId)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/sign-in", new { userId, passcode = Passcode });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (cookie, body.GetProperty("csrfToken").GetString()!);
    }

    private static async Task<HttpResponseMessage> PostPackageAsync(HttpClient client, (string Cookie, string Csrf) session, string fileName, byte[] bytes)
    {
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(part, "files", fileName);

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/cases/{Case}/packages") { Content = form };
        request.Headers.Add("Cookie", session.Cookie);
        request.Headers.Add("X-CSRF-Token", session.Csrf);
        return await client.SendAsync(request);
    }

    private CaseScope ScopeFor(ICaseStateStore store) =>
        store.OwnerOf(Case) is { } owner ? store.AuthorizeCase(Case, owner) : store.CreateCase(Case, "presenter");

    [Fact]
    public async Task PackageWithoutAcceptedDocumentsDoesNotInvokeModelOrWriteTrace()
    {
        var model = new CountingChatClient();
        using var factory = CreateFactory(model);
        using var client = factory.CreateClient();
        var presenter = await SignInAsync(client, "presenter");

        // A PDF header with no body is not an accepted document.
        var notAPdf = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D };
        using var response = await PostPackageAsync(client, presenter, "broken.pdf", notAPdf);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var package = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.DoesNotContain("accepted", package.GetProperty("documents").EnumerateArray()
            .Select(d => d.GetProperty("status").GetString()));
        Assert.Equal(0, model.Calls);

        var packageId = package.GetProperty("packageId").GetString()!;
        var store = new CaseStateStore(_statePath);
        Assert.Null(store.FindTrace(ScopeFor(store), Case, packageId, "inv-" + packageId));
    }

    // AC: TRACE-PRODUCER-01
    [Fact]
    public async Task AcceptedEvidenceRunsInvestigationAndPersistsTrace()
    {
        var model = new CountingChatClient();
        using var factory = CreateFactory(model);
        using var client = factory.CreateClient();
        var presenter = await SignInAsync(client, "presenter");

        using var response = await PostPackageAsync(client, presenter, "synthetic-evidence-a.pdf", ReadFixture("synthetic-evidence-a.pdf"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var packageId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("packageId").GetString()!;

        Assert.Equal(1, model.Calls);
        var store = new CaseStateStore(_statePath);
        var trace = store.FindTrace(ScopeFor(store), Case, packageId, "inv-" + packageId);
        Assert.NotNull(trace);
        Assert.Equal("rejected", trace.Outcome);
        Assert.Empty(trace.Steps);
    }

    // AC: TRACE-PRODUCER-03
    [Fact]
    public async Task TraceAppendFailureCannotReturnSuccessfulSubmission()
    {
        var proxy = DispatchProxy.Create<ICaseStateStore, AppendFailingStore>();
        var failing = (AppendFailingStore)(object)proxy;
        failing.Inner = new CaseStateStore(_statePath);
        var model = new CountingChatClient();
        using var factory = CreateFactory(model, proxy);
        using var client = factory.CreateClient();
        var presenter = await SignInAsync(client, "presenter");
        var pdf = ReadFixture("synthetic-evidence-a.pdf");

        // The failure reaches the caller: the submission is not reported as successful.
        using var first = await PostPackageAsync(client, presenter, "synthetic-evidence-a.pdf", pdf);
        Assert.Equal(HttpStatusCode.InternalServerError, first.StatusCode);
        var runId = failing.AttemptedRunId!;
        var store = new CaseStateStore(_statePath);
        Assert.Null(store.FindTrace(ScopeFor(store), Case, runId, "inv-" + runId));

        // A retry of the same package must not succeed while its trace is absent.
        using var retry = await PostPackageAsync(client, presenter, "synthetic-evidence-a.pdf", pdf);
        Assert.Equal(HttpStatusCode.InternalServerError, retry.StatusCode);
        Assert.Equal(2, model.Calls);
        Assert.Null(store.FindTrace(ScopeFor(store), Case, runId, "inv-" + runId));

        // Once the append succeeds, the retry completes and the trace exists.
        failing.FailAppends = false;
        using var recovered = await PostPackageAsync(client, presenter, "synthetic-evidence-a.pdf", pdf);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        var recoveredStore = new CaseStateStore(_statePath);
        Assert.NotNull(recoveredStore.FindTrace(ScopeFor(recoveredStore), Case, runId, "inv-" + runId));
    }

    // AC: TRACE-PRODUCER-07
    [Fact]
    public async Task NonOwnerSubmissionIsRefusedBeforeAnythingIsSaved()
    {
        // The seeded fixture has one presenter, so the case is owned by another user before the presenter submits.
        new CaseStateStore(_statePath).CreateCase(Case, "fin-1");
        var model = new CountingChatClient();
        using var factory = CreateFactory(model);
        using var client = factory.CreateClient();
        var presenter = await SignInAsync(client, "presenter");

        using var response = await PostPackageAsync(client, presenter, "valid-text.pdf", ReadFixture("valid-text.pdf"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        Assert.Equal(0, model.Calls);
        var basis = factory.Services.GetRequiredService<AirlineDemo.Api.Evidence.EvidenceBasisStore>();
        Assert.Empty(basis.History($"{Case}/valid-text.pdf"));
        var packageId = AirlineDemo.Api.Evidence.EvidenceEndpoints.PackageIdFor(Case,
            [new AirlineDemo.Api.Evidence.EvidenceDocument("valid-text.pdf", ReadFixture("valid-text.pdf"))]);
        var store = new CaseStateStore(_statePath);
        Assert.Null(store.FindTrace(ScopeFor(store), Case, packageId, "inv-" + packageId));
        Assert.Null(factory.Services.GetRequiredService<AirlineDemo.Api.Evidence.EvidencePackageStore>().Find(Case, packageId));
    }

    // AC: TRACE-PRODUCER-08
    [Fact]
    public async Task DuplicateAcceptedFilenamesInvestigateEachNameOnceAtItsNewestVersion()
    {
        var model = new CountingChatClient();
        using var factory = CreateFactory(model);
        using var client = factory.CreateClient();
        var presenter = await SignInAsync(client, "presenter");

        var form = new MultipartFormDataContent();
        var first = new ByteArrayContent(ReadFixture("synthetic-evidence-a.pdf"));
        first.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(first, "files", "same.pdf");
        var second = new ByteArrayContent(ReadFixture("synthetic-evidence-b.pdf"));
        second.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(second, "files", "same.pdf");
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/cases/{Case}/packages") { Content = form };
        request.Headers.Add("Cookie", presenter.Cookie);
        request.Headers.Add("X-CSRF-Token", presenter.Csrf);

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var packageId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("packageId").GetString()!;
        Assert.Equal(1, model.Calls);

        var basis = factory.Services.GetRequiredService<AirlineDemo.Api.Evidence.EvidenceBasisStore>();
        var newest = basis.History($"{Case}/same.pdf").Max(v => v.Version);
        var store = new CaseStateStore(_statePath);
        var trace = store.FindTrace(ScopeFor(store), Case, packageId, "inv-" + packageId);
        Assert.NotNull(trace);
        Assert.Equal(newest, trace.BasisVersion);
    }

    // AC: TRACE-PRODUCER-04
    [Fact]
    public async Task RecordsStartedOutcomeAndSkipsPreStartRejection()
    {
        var model = new CountingChatClient();
        using var factory = CreateFactory(model);
        using var client = factory.CreateClient();
        var presenter = await SignInAsync(client, "presenter");

        // Rejected before the investigation starts: no model call and no trace.
        using var rejected = await PostPackageAsync(client, presenter, "broken.pdf", new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D });
        Assert.Equal(HttpStatusCode.Created, rejected.StatusCode);
        var rejectedId = (await rejected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("packageId").GetString()!;
        Assert.Equal(0, model.Calls);
        var store = new CaseStateStore(_statePath);
        Assert.Null(store.FindTrace(ScopeFor(store), Case, rejectedId, "inv-" + rejectedId));

        // Started, with no tool or citation activity: the outcome is still recorded, with no steps and no handover.
        using var accepted = await PostPackageAsync(client, presenter, "synthetic-evidence-a.pdf", ReadFixture("synthetic-evidence-a.pdf"));
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        var acceptedId = (await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("packageId").GetString()!;
        Assert.Equal(1, model.Calls);
        var started = new CaseStateStore(_statePath);
        var trace = started.FindTrace(ScopeFor(started), Case, acceptedId, "inv-" + acceptedId);
        Assert.NotNull(trace);
        Assert.Equal("rejected", trace.Outcome);
        Assert.Empty(trace.Steps);

        // A completed investigation records its outcome and the deterministic-policy handover.
        var claim = """[{"text":"Synthetic evidence B.","witnesses":[{"documentId":"synthetic-evidence-b.pdf","page":1,"quote":"Synthetic evidence B."}]}]""";
        using var completingFactory = CreateFactory(new CountingChatClient(claim));
        using var completingClient = completingFactory.CreateClient();
        var completingPresenter = await SignInAsync(completingClient, "presenter");
        using var completed = await PostPackageAsync(completingClient, completingPresenter, "synthetic-evidence-b.pdf", ReadFixture("synthetic-evidence-b.pdf"));
        Assert.Equal(HttpStatusCode.Created, completed.StatusCode);
        var completedId = (await completed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("packageId").GetString()!;
        var completedStore = new CaseStateStore(_statePath);
        var completedTrace = completedStore.FindTrace(ScopeFor(completedStore), Case, completedId, "inv-" + completedId);
        Assert.NotNull(completedTrace);
        Assert.Equal("approved", completedTrace.Outcome);
        Assert.Equal(InvestigationTraceRecorder.DeterministicPolicy, completedTrace.Steps[^1].PolicyHandover);
    }
}

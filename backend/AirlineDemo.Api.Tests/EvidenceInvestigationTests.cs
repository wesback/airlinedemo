using System.Net;
using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Net.Sockets;
using System.Text;
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

    // Records the case and accepted documents the endpoint asks the case-scoped source for, then returns the real source.
    private sealed class RecordingSourceFactory(AirlineDemo.Api.Evidence.EvidenceBasisStore basis) : AirlineDemo.Api.Evidence.EvidenceCaseReadSourceFactory(basis)
    {
        public List<(string CaseId, string[] Accepted, Dictionary<string, int> Pinned)> Requests { get; } = [];

        public override ICaseReadSource Create(string caseId, IReadOnlyList<string> acceptedFileNames, IReadOnlyDictionary<string, int> pinnedVersions)
        {
            Requests.Add((caseId, [.. acceptedFileNames], new Dictionary<string, int>(pinnedVersions)));
            return base.Create(caseId, acceptedFileNames, pinnedVersions);
        }
    }

    private WebApplicationFactory<Program> CreateFactory(IChatClient client, ICaseStateStore? store = null, AirlineDemo.Api.Evidence.EvidenceCaseReadSourceFactory? sources = null) =>
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
                if (sources is not null) services.AddSingleton<AirlineDemo.Api.Evidence.EvidenceCaseReadSourceFactory>(sources);
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
        var sources = new RecordingSourceFactory(new AirlineDemo.Api.Evidence.EvidenceBasisStore());
        using var factory = CreateFactory(model, sources: sources);
        using var client = factory.CreateClient();
        var presenter = await SignInAsync(client, "presenter");

        using var response = await PostPackageAsync(client, presenter, "synthetic-evidence-a.pdf", ReadFixture("synthetic-evidence-a.pdf"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var packageId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("packageId").GetString()!;

        Assert.Equal(1, model.Calls);
        var request = Assert.Single(sources.Requests);
        Assert.Equal(Case, request.CaseId);
        Assert.Equal(["synthetic-evidence-a.pdf"], request.Accepted);
        Assert.Equal(1, request.Pinned["synthetic-evidence-a.pdf"]);
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
        Assert.Equal("escalate", trace.Decision);
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

    // AC: TRACE-PRODUCER-09
    [Fact]
    public async Task AcceptedEvidenceRunsScriptedToolCallsAndPersistsOrderedEvents()
    {
        const string fileName = "return-status.pdf";
        var statusText = ReturnStatusFixture();
        // The gate only sends synthetic-marked content, so the fixture sentence doubles as the document id the inventory lists.
        var claim = $$"""[{"text":"Return readiness is pending reserve approval.","witnesses":[{"documentId":"{{statusText}}","page":1,"quote":"{{statusText}}"}]}]""";
        var model = new ScriptedReplyClient(
            ToolCall("c1", CaseReadTools.InventoryTool, null),
            ToolCall("c2", CaseReadTools.SourcePageTool, new Dictionary<string, object?> { ["documentId"] = statusText, ["page"] = 1 }),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, claim)));
        var sources = new FixtureSourceFactory(new AirlineDemo.Api.Evidence.EvidenceBasisStore(), statusText, statusText);
        using var factory = CreateFactory(model, sources: sources);
        using var client = factory.CreateClient();
        var presenter = await SignInAsync(client, "presenter");

        using var response = await PostPackageAsync(client, presenter, fileName, SyntheticPdf(statusText));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var packageId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("packageId").GetString()!;
        var store = new CaseStateStore(_statePath);
        var trace = store.FindTrace(ScopeFor(store), Case, packageId, "inv-" + packageId);
        Assert.Equal(3, model.Calls);
        Assert.NotNull(trace);
        Assert.Equal("approved", trace.Outcome);
        Assert.Equal(
            new[] { CaseReadTools.InventoryTool, CaseReadTools.SourcePageTool, "citation-check", "claim-validation", "policy-handover" },
            trace.Steps.Select(s => s.ToolName).ToArray());
        Assert.Equal("deterministic-policy", trace.Steps[^1].PolicyHandover);
    }

    // AC: TRACE-PRODUCER-11
    [Fact]
    public async Task HostRegistersAzureOpenAIChatClientWithoutCallingIt()
    {
        // The fake transport counts every request that reaches the wire; resolving the client must send none.
        var credentialsCreated = 0;
        var requestsSent = 0;
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Demo:Passcode"] = Passcode,
                    ["CaseState:Path"] = _statePath,
                    ["ModelGateway:Endpoint"] = "https://gateway-demo.private.invalid/",
                }));
            // Counts every credential the host creates through its only production factory.
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(FakeApprovedManifest.Approved());
                services.AddSingleton<Func<Azure.Core.TokenCredential>>(_ => () =>
                {
                    Interlocked.Increment(ref credentialsCreated);
                    return new StaticTokenCredential(new Azure.Core.AccessToken("fake-token", DateTimeOffset.UtcNow.AddHours(1)));
                });
                services.AddSingleton<HttpMessageHandler>(new FakeOpenAiHandler(() => Interlocked.Increment(ref requestsSent)));
            });
        });

        var services = factory.Services;
        using var azureEvents = new AzureIdentityEventRecorder();
        var eventsBefore = azureEvents.Events.Count;

        var chatClient = services.GetRequiredService<IChatClient>();

        Assert.NotNull(chatClient);
        Assert.Equal(0, Volatile.Read(ref credentialsCreated));
        Assert.Equal(0, Volatile.Read(ref requestsSent));
        Assert.Empty(azureEvents.Events.Skip(eventsBefore));

        // The first request fetches a token exactly once and then reaches the fake transport, which refuses it with 400 (no retry).
        await Assert.ThrowsAnyAsync<Exception>(() => chatClient.GetResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "ping") },
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, Volatile.Read(ref credentialsCreated));
        Assert.Equal(1, Volatile.Read(ref requestsSent));
    }

    private sealed class FakeOpenAiHandler(Action onRequest) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onRequest();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{\"error\":{\"code\":\"fake\",\"message\":\"fake transport\"}}", Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public async Task LazyCredentialCreatesOnceOnFirstTokenRequest()
    {
        var created = 0;
        var expires = DateTimeOffset.UtcNow.AddHours(1);
        var credential = new LazyTokenCredential(() =>
        {
            Interlocked.Increment(ref created);
            return new StaticTokenCredential(new Azure.Core.AccessToken("token-value", expires));
        });
        var context = new Azure.Core.TokenRequestContext(new[] { "https://cognitiveservices.azure.com/.default" });

        Assert.Equal(0, Volatile.Read(ref created));

        var first = credential.GetToken(context, CancellationToken.None);
        var second = await credential.GetTokenAsync(context, CancellationToken.None);

        Assert.Equal("token-value", first.Token);
        Assert.Equal(expires, first.ExpiresOn);
        Assert.Equal("token-value", second.Token);
        Assert.Equal(expires, second.ExpiresOn);
        Assert.Equal(1, Volatile.Read(ref created));
    }

    // Reads the committed synthetic fixture that the endpoint-flow test is built on.
    private static string ReturnStatusFixture([CallerFilePath] string testFile = "")
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "AirlineDemo.Api", "Investigation", "SyntheticFixtures", "return-status.txt"));
        return File.ReadAllText(path).Trim();
    }

    // Returns one fixed token, so the lazy wrapper's token methods can be checked without Azure.
    private sealed class StaticTokenCredential(Azure.Core.AccessToken token) : Azure.Core.TokenCredential
    {
        public override Azure.Core.AccessToken GetToken(Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken) => token;

        public override ValueTask<Azure.Core.AccessToken> GetTokenAsync(Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(token);
    }

    // Records Azure.Identity diagnostics; a token request or credential use made while resolving the client shows up here.
    private sealed class AzureIdentityEventRecorder : EventListener
    {
        public ConcurrentQueue<string> Events { get; } = new();

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "Azure-Identity")
            {
                EnableEvents(eventSource, EventLevel.Verbose, EventKeywords.All);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData) =>
            Events.Enqueue(eventData.EventSource.Name + "/" + eventData.EventName);
    }

    private static ChatResponse ToolCall(string callId, string name, IDictionary<string, object?>? args) =>
        new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(callId, name, args)]));

    // Builds a one-page PDF whose page text is exactly the given line.
    private static byte[] SyntheticPdf(string text)
    {
        var stream = $"BT /F1 12 Tf 72 720 Td ({text}) Tj ET";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {stream.Length} >>\nstream\n{stream}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            pdf.Append($"{offset:D10} 00000 n \n");
        }

        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    private sealed class FixtureCaseSource(string documentId, string text) : ICaseReadSource
    {
        public IReadOnlyList<string> GetInventory(string caseId) => [documentId];

        public string? GetApprovedRequirements(string caseId) => null;

        public string? GetSourcePage(string caseId, string requestedDocumentId, int page) =>
            requestedDocumentId == documentId && page == 1 ? text : null;

        public IReadOnlyList<string> SearchWithinBasis(string caseId, string query) => [];

        public string? GetProcessingStatus(string caseId) => null;
    }

    private sealed class FixtureSourceFactory(AirlineDemo.Api.Evidence.EvidenceBasisStore basis, string documentId, string text) : AirlineDemo.Api.Evidence.EvidenceCaseReadSourceFactory(basis)
    {
        public override ICaseReadSource Create(string caseId, IReadOnlyList<string> acceptedFileNames, IReadOnlyDictionary<string, int> pinnedVersions) =>
            new FixtureCaseSource(documentId, text);
    }

    /// <summary>Model stub that returns the scripted replies in order and counts its calls. Makes no network request.</summary>
    private sealed class ScriptedReplyClient(params ChatResponse[] replies) : IChatClient
    {
        public int Calls { get; private set; }

        public ChatClientMetadata Metadata { get; } = new("scripted");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(replies[Calls++]);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private static readonly string[] DemoRecordFiles = ["engine-identity-record.pdf", "llp-status-sheet.pdf", "maintenance-log-extract.pdf"];

    // The demo records package the presenter sends from the intake step, served by the client from frontend/public.
    private static byte[] DemoRecord(string package, string file, [CallerFilePath] string testFile = "") =>
        File.ReadAllBytes(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "..", "frontend", "public", "demo-records", package, file)));

    private static async Task<HttpResponseMessage> PostDemoPackageAsync(HttpClient client, (string Cookie, string Csrf) session, string package)
    {
        var form = new MultipartFormDataContent();
        foreach (var file in DemoRecordFiles)
        {
            var part = new ByteArrayContent(DemoRecord(package, file));
            part.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            form.Add(part, "files", file);
        }

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/cases/{Case}/packages") { Content = form };
        request.Headers.Add("Cookie", session.Cookie);
        request.Headers.Add("X-CSRF-Token", session.Csrf);
        return await client.SendAsync(request);
    }

    // The model gate sends only registered synthetic text, so every demo PDF must extract to a committed fixture.
    [Theory]
    [InlineData("case-001", "engine-identity-record.pdf")]
    [InlineData("case-001", "llp-status-sheet.pdf")]
    [InlineData("case-001", "maintenance-log-extract.pdf")]
    [InlineData("case-002", "engine-identity-record.pdf")]
    [InlineData("case-002", "llp-status-sheet.pdf")]
    [InlineData("case-002", "maintenance-log-extract.pdf")]
    public void DemoRecordExtractsToRegisteredSyntheticContent(string package, string file)
    {
        var extracted = AirlineDemo.Api.Evidence.EvidenceIntake.ExtractText(DemoRecord(package, file));
        Assert.Equal(AirlineDemo.Api.Evidence.EvidenceProcessingState.Complete, extracted.State);
        Assert.True(SyntheticContentRegistry.FromCommittedFixtures().IsRegistered(extracted.Text.TrimEnd('\r', '\n')));
    }

    [Fact]
    public async Task DemoPackageMovesTheCaseThroughIntakeAndInvestigationToTechnicalReview()
    {
        const string quote = "No installation record for ESN-DEMO1-212 is included in this records package.";
        var claim = $$"""[{"text":"{{quote}}","witnesses":[{"documentId":"llp-status-sheet.pdf","page":1,"quote":"{{quote}}"}]}]""";
        var model = new CountingChatClient(claim);
        using var factory = CreateFactory(model);
        using var client = factory.CreateClient();
        var presenter = await SignInAsync(client, "presenter");

        using var response = await PostDemoPackageAsync(client, presenter, "case-001");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var packageId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("packageId").GetString()!;
        Assert.Equal(1, model.Calls);

        var store = new CaseStateStore(_statePath);
        var state = store.Read(ScopeFor(store));
        Assert.Single(state.Basis);
        var events = state.JourneyEvents!;
        Assert.Equal(["package-received", "evidence-reviewed", "finding-raised"], events.Select(e => e.EventType));
        Assert.Equal(["intake", "records-agent", "records-agent"], events.Select(e => e.ActorRole));
        Assert.Equal(["Rules-based", "AI agent", "AI agent"], events.Select(e => e.CapabilityLabel));
        Assert.Equal(DemoRecordFiles, events[0].Witnesses.Select(w => w.DocumentId).Order(StringComparer.Ordinal));
        Assert.Equal(quote, events[2].Summary);
        Assert.Equal(("llp-status-sheet.pdf", 1, 1), (events[2].Witnesses.Single().DocumentId, events[2].Witnesses.Single().Page, events[2].Witnesses.Single().BasisVersion));
        var item = Assert.Single(state.WorkItems!);
        Assert.Equal(("review-case-001", "open", "technical-reviewer"), (item.ItemId, item.State, item.Owner));
        Assert.Equal("approved", store.FindTrace(ScopeFor(store), Case, packageId, "inv-" + packageId)!.Outcome);

        // A repeated submission replays the receipt: no second basis, event, work item or model call.
        using var repeat = await PostDemoPackageAsync(client, presenter, "case-001");
        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        var after = new CaseStateStore(_statePath);
        var replayed = after.Read(ScopeFor(after));
        Assert.Equal(state.Revision, replayed.Revision);
        Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task ModelServiceFailureEscalatesToTechnicalReviewInsteadOfFailingTheUpload()
    {
        using var factory = CreateFactory(new FailingModelClient());
        using var client = factory.CreateClient();
        var presenter = await SignInAsync(client, "presenter");

        using var response = await PostDemoPackageAsync(client, presenter, "case-001");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var packageId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("packageId").GetString()!;

        var store = new CaseStateStore(_statePath);
        var state = store.Read(ScopeFor(store));
        Assert.Equal(["package-received", "investigation-escalated"], state.JourneyEvents!.Select(e => e.EventType));
        Assert.StartsWith("The AI agent could not reach the model service", state.JourneyEvents![1].Summary);
        Assert.Equal("review-case-001", Assert.Single(state.WorkItems!).ItemId);
        Assert.Equal("escalated", store.FindTrace(ScopeFor(store), Case, packageId, "inv-" + packageId)!.Outcome);
    }

    [Fact]
    public async Task UnregisteredDocumentIsPinnedButNeverSentToTheModel()
    {
        var model = new CountingChatClient();
        using var factory = CreateFactory(model);
        using var client = factory.CreateClient();
        var presenter = await SignInAsync(client, "presenter");

        using var response = await PostPackageAsync(client, presenter, "valid-text.pdf", ReadFixture("valid-text.pdf"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        Assert.Equal(0, model.Calls);
        var store = new CaseStateStore(_statePath);
        var state = store.Read(ScopeFor(store));
        Assert.Single(state.Basis);
        Assert.Equal(["package-received", "investigation-escalated"], state.JourneyEvents!.Select(e => e.EventType));
        Assert.StartsWith("The model gate kept these documents from the AI agent", state.JourneyEvents![1].Summary);
        Assert.Single(state.WorkItems!);
    }

    /// <summary>Model stub that fails like an unreachable or refusing model endpoint. Makes no network request.</summary>
    private sealed class FailingModelClient : IChatClient
    {
        public ChatClientMetadata Metadata { get; } = new("failing");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new System.ClientModel.ClientResultException("HTTP 400 (invalid_request_error) model refused the request");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    [Fact]
    public void EvidenceSourceReturnsOnlyTheCitedPageOfAMultiPageDocument()
    {
        var extracted = AirlineDemo.Api.Evidence.EvidenceIntake.ExtractText(ReadFixture("two-pages.pdf"));
        Assert.Equal(AirlineDemo.Api.Evidence.EvidenceProcessingState.Complete, extracted.State);
        var basis = new AirlineDemo.Api.Evidence.EvidenceBasisStore();
        basis.CommitAll([($"{Case}/two.pdf", extracted.Text)]);
        var source = new AirlineDemo.Api.Evidence.EvidenceCaseReadSourceFactory(basis)
            .Create(Case, ["two.pdf"], new Dictionary<string, int> { ["two.pdf"] = 1 });

        Assert.Contains("Alpha only on page one.", source.GetSourcePage(Case, "two.pdf", 1));
        Assert.DoesNotContain("Beta", source.GetSourcePage(Case, "two.pdf", 1));
        Assert.Contains("Beta only on page two.", source.GetSourcePage(Case, "two.pdf", 2));
        Assert.DoesNotContain("Alpha", source.GetSourcePage(Case, "two.pdf", 2));
        Assert.Null(source.GetSourcePage(Case, "two.pdf", 3));
        Assert.Null(source.GetSourcePage(Case, "two.pdf", 0));
    }

    [Fact]
    public void CitationToALaterPageIsSupportedNotDowngraded()
    {
        var extracted = AirlineDemo.Api.Evidence.EvidenceIntake.ExtractText(ReadFixture("two-pages.pdf"));
        var basis = new Dictionary<string, BasisDocument> { ["two.pdf"] = AirlineDemo.Api.Evidence.EvidenceEndpoints.BasisDocumentFor("two.pdf", extracted.Text) };

        var onPageTwo = ClaimValidator.Validate(
            new InvestigationClaim("Beta only on page two.", [new DocumentWitness("two.pdf", 2, "Beta only on page two.")]), ClaimKind.Baseline, basis, "Beta");
        var onWrongPage = ClaimValidator.Validate(
            new InvestigationClaim("Beta only on page two.", [new DocumentWitness("two.pdf", 1, "Beta only on page two.")]), ClaimKind.Baseline, basis, "Beta");

        Assert.Equal(ClaimStatus.Supported, onPageTwo.Status);
        Assert.Equal(ClaimStatus.Downgraded, onWrongPage.Status);
    }
}

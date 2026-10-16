using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AirlineDemo.Api.Admission;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Evidence;
using AirlineDemo.Api.Evidence.PdfWorker;
using AirlineDemo.Api.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AirlineDemo.Api.Tests;

public sealed class WorkloadAdmissionLimitsTests
{
    private const string Passcode = "test-passcode";
    private const string Case = "case-001";
    private const string OtherCase = "case-002";
    private const string SessionCookieName = "__Host-demo-session";

    private sealed record Session(string Cookie, string Csrf);

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    // A model stand-in whose investigations hold their admission slot inside the model call until the test releases,
    // fails or cancels the current gate.
    private sealed class GatedChatClient : IChatClient
    {
        private int _entered;

        public TaskCompletionSource Gate { get; set; } = NewGate();

        public int Entered => Volatile.Read(ref _entered);

        public ChatClientMetadata Metadata { get; } = new("gated");

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _entered);
            await Gate.Task.WaitAsync(cancellationToken);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "[]"));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 1000 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    private static WebApplicationFactory<Program> CreateFactory(WorkloadAdmissionLimits limits, IChatClient? chat = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["Demo:Passcode"] = Passcode }));
            builder.ConfigureTestServices(services => services.AddSingleton<IChatClient>(chat ?? new CountingChatClient()));
            builder.ConfigureTestServices(services => services.AddSingleton(FakeApprovedManifest.Approved()));
            // The test host runs as root on Linux, so point the isolated PDF worker at this test assembly.
            builder.ConfigureTestServices(services => services.AddSingleton(new IsolatedPdfParserOptions
            {
                WorkerFileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
                WorkerArguments = new[] { typeof(IsolatedPdfParser).Assembly.Location, PdfWorkerEntry.Switch },
            }));
            builder.ConfigureTestServices(services => services.AddSingleton(limits));
        });

    private static async Task<Session> SignInAsync(HttpClient client, string userId)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/sign-in", new { userId, passcode = Passcode });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new Session(cookie, body.GetProperty("csrfToken").GetString()!);
    }

    // A second authenticated presenter session, created directly in the server's session store.
    private static Session AddPresenterSession(WebApplicationFactory<Program> factory, string userId)
    {
        var session = factory.Services.GetRequiredService<DemoSessionStore>()
            .Create(new DemoUser(userId, "Second presenter", EvidenceEndpoints.PresenterRole, [Case]));
        return new Session($"{SessionCookieName}={session.Id}", session.CsrfToken);
    }

    private static byte[] FakePdf(int size)
    {
        var bytes = new byte[size];
        Encoding.ASCII.GetBytes("%PDF-").CopyTo(bytes, 0);
        return bytes;
    }

    // A one-page text PDF with a valid cross-reference table, so the endpoint accepts it and starts an investigation.
    // Extra comment bytes make each document distinct, so each submission creates its own package.
    // The page text must be registered synthetic content, or the model gate refuses the investigation before the model is called.
    private static string RegisteredEvidenceText([System.Runtime.CompilerServices.CallerFilePath] string testFile = "")
    {
        var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(testFile)!, "..", "AirlineDemo.Api", "Investigation", "SyntheticFixtures", "return-status.txt"));
        return System.IO.File.ReadAllText(path).Trim();
    }

    private static byte[] TextPdf(int extraBytes)
    {
        var sb = new StringBuilder("%PDF-1.4\n");
        if (extraBytes > 0) sb.Append('%', extraBytes).Append('\n');
        var offsets = new List<int>();
        void Add(string body)
        {
            offsets.Add(sb.Length);
            sb.Append(offsets.Count).Append(" 0 obj ").Append(body).Append('\n');
        }

        var content = $"BT /F1 12 Tf 72 720 Td ({RegisteredEvidenceText()}) Tj ET";
        Add("<< /Type /Catalog /Pages 2 0 R >> endobj");
        Add("<< /Type /Pages /Count 1 /Kids [3 0 R] >> endobj");
        Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >> endobj");
        Add($"<< /Length {content.Length} >> stream\n{content}\nendstream endobj");
        Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> endobj");
        var xrefPosition = sb.Length;
        sb.Append("xref\n0 ").Append(offsets.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            sb.Append(offset.ToString("D10")).Append(" 00000 n \n");
        }

        sb.Append("trailer << /Size ").Append(offsets.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n")
            .Append(xrefPosition.ToString("D10")).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static Task<HttpResponseMessage> PostPackageAsync(HttpClient client, Session session, string? forgedUserId = null, int size = 1024, byte[]? bytes = null, CancellationToken ct = default, string caseId = Case)
    {
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes ?? FakePdf(size));
        part.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(part, EvidenceEndpoints.FormFieldName, "statement.pdf");
        if (forgedUserId is not null) form.Add(new StringContent(forgedUserId), "userId");

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/cases/{caseId}/packages") { Content = form };
        request.Headers.Add("Cookie", session.Cookie);
        request.Headers.Add("X-CSRF-Token", session.Csrf);
        return client.SendAsync(request, ct);
    }

    // Reads the same stores the endpoint writes to: a rejected submission must leave none of this state behind.
    private static void AssertNoPackage(WebApplicationFactory<Program> factory, byte[] bytes) =>
        Assert.Null(factory.Services.GetRequiredService<EvidencePackageStore>()
            .Find(Case, EvidenceEndpoints.PackageIdFor(Case, [new EvidenceDocument("statement.pdf", bytes)])));

    private static void AssertNoPackage(WebApplicationFactory<Program> factory, int size) =>
        AssertNoPackage(factory, FakePdf(size));

    // An investigation result is keyed by its package run id; a rejected submission must leave no trace under that key.
    private static void AssertNoInvestigationResult(WebApplicationFactory<Program> factory, byte[] bytes)
    {
        var store = factory.Services.GetRequiredService<ICaseStateStore>();
        var packageId = EvidenceEndpoints.PackageIdFor(Case, [new EvidenceDocument("statement.pdf", bytes)]);
        Assert.Null(store.FindTrace(store.AuthorizeCase(Case, "presenter"), Case, packageId, "inv-" + packageId));
    }

    private static int BasisCount(WebApplicationFactory<Program> factory) =>
        factory.Services.GetRequiredService<EvidenceBasisStore>().History($"{Case}/statement.pdf").Count;

    // True only when both investigation slots are free; any slot it takes is released before returning.
    private static bool TwoSlotsFree(WorkloadAdmissionLimits limits)
    {
        using var first = limits.TryAdmitInvestigation();
        using var second = first is null ? null : limits.TryAdmitInvestigation();
        return first is not null && second is not null;
    }

    [Fact]
    public async Task Package_rate_limit_uses_authenticated_session_and_rolling_window()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var limits = new WorkloadAdmissionLimits(clock);
        using var factory = CreateFactory(limits);
        using var client = factory.CreateClient();
        var presenter = await SignInAsync(client, "presenter");
        factory.Services.GetRequiredService<DemoSessionStore>().GrantScope(presenter.Cookie.Split('=')[1], OtherCase);
        var second = AddPresenterSession(factory, "presenter-2");

        // The forged multipart userId names the other session; it must not change this caller's quota.
        for (var i = 0; i < WorkloadAdmissionLimits.PackageAttemptsPerWindow; i++)
        {
            var caseId = i % 2 == 0 ? Case : OtherCase;
            using var admitted = await PostPackageAsync(client, presenter, forgedUserId: "presenter-2", size: 1024 + i, caseId: caseId);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, admitted.StatusCode);
        }

        var basisBefore = BasisCount(factory);
        using var rejected = await PostPackageAsync(client, presenter, forgedUserId: "presenter-2", size: 2048);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        var error = await rejected.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("package submission limit exceeded", error.GetProperty("error").GetString());
        Assert.False(error.TryGetProperty("packageId", out _));
        var refusal = Assert.Single(limits.Events);
        Assert.Equal(WorkloadAdmissionLimits.PackageKind, refusal.Kind);
        Assert.Equal("presenter", refusal.Subject);
        Assert.Equal(basisBefore, BasisCount(factory));
        AssertNoPackage(factory, 2048);

        // The other session has its own quota and is still admitted.
        using var independent = await PostPackageAsync(client, second, size: 3000);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, independent.StatusCode);

        clock.Advance(WorkloadAdmissionLimits.PackageWindow);
        using var afterWindow = await PostPackageAsync(client, presenter, size: 4000);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, afterWindow.StatusCode);
    }

    [Fact]
    public async Task Concurrent_investigations_are_bounded_and_release_slots()
    {
        var limits = new WorkloadAdmissionLimits(TimeProvider.System);
        var chat = new GatedChatClient();
        using var factory = CreateFactory(limits, chat);
        using var client = factory.CreateClient();
        var presenter = await SignInAsync(client, "presenter");
        // Two endpoint submissions hold both investigation slots inside the model call until the gate is released.
        var admitted = new[]
        {
            PostPackageAsync(client, presenter, bytes: TextPdf(1)),
            PostPackageAsync(client, presenter, bytes: TextPdf(2)),
        };
        await Task.WhenAny(admitted[0], admitted[1], Task.Delay(2000));
        foreach (var early in admitted.Where(t => t.IsCompleted))
        {
            using var r = await early;
            Assert.Fail($"admitted submission finished early: {(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}");
        }
        await WaitUntilAsync(() => chat.Entered == 2);

        // A third submission is refused before any case, package, basis entry or investigation result is written.
        var otherPresenter = AddPresenterSession(factory, "presenter-2");
        using var refused = await PostPackageAsync(client, otherPresenter, bytes: TextPdf(3));
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        var refusedBody = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(WorkloadAdmissionLimits.InvestigationLimitError, refusedBody.GetProperty("error").GetString());
        var refusal = Assert.Single(limits.Events);
        Assert.Equal(WorkloadAdmissionLimits.InvestigationKind, refusal.Kind);
        Assert.Equal(WorkloadAdmissionLimits.InvestigationLimitError, refusal.Error);
        Assert.Equal(2, chat.Entered);
        AssertNoPackage(factory, TextPdf(3));
        AssertNoInvestigationResult(factory, TextPdf(3));

        // Success: the admitted investigations finish through the endpoint and release their slots.
        chat.Gate.SetResult();
        foreach (var pending in admitted)
        {
            using var done = await pending;
            Assert.NotEqual(HttpStatusCode.TooManyRequests, done.StatusCode);
        }

        await WaitUntilAsync(() => TwoSlotsFree(limits));

        // Cancellation: the endpoint holds its slot until the cancelled investigation exits, then releases it.
        chat.Gate = NewGate();
        using var cts = new CancellationTokenSource();
        var cancelled = PostPackageAsync(client, presenter, bytes: TextPdf(4), ct: cts.Token);
        await WaitUntilAsync(() => chat.Entered == 3);
        Assert.False(TwoSlotsFree(limits));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        await WaitUntilAsync(() => TwoSlotsFree(limits));

        // Exception: the endpoint releases its slot when the investigation throws.
        chat.Gate = NewGate();
        var faulted = PostPackageAsync(client, presenter, bytes: TextPdf(5));
        await WaitUntilAsync(() => chat.Entered == 4);
        chat.Gate.SetException(new InvalidOperationException("boom"));
        using var failed = await faulted;
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        await WaitUntilAsync(() => TwoSlotsFree(limits));

        // After every release the endpoint is admitted again.
        using var admittedAgain = await PostPackageAsync(client, presenter, size: 2048);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, admittedAgain.StatusCode);
    }

    [Fact]
    public async Task Limit_failures_are_recorded_without_partial_admission_state()
    {
        var limits = new WorkloadAdmissionLimits(new ManualClock(DateTimeOffset.UtcNow));
        using var factory = CreateFactory(limits);
        using var client = factory.CreateClient();
        var presenter = await SignInAsync(client, "presenter");

        // Exhaust the presenter's package quota, then submit: rejected with no package created.
        for (var i = 0; i < WorkloadAdmissionLimits.PackageAttemptsPerWindow; i++)
        {
            Assert.True(limits.TryAdmitPackage("presenter"));
        }

        using var rateRejected = await PostPackageAsync(client, presenter, size: 2048);
        Assert.Equal(HttpStatusCode.TooManyRequests, rateRejected.StatusCode);
        var rateBody = await rateRejected.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("package submission limit exceeded", rateBody.GetProperty("error").GetString());
        Assert.False(rateBody.TryGetProperty("packageId", out _));
        AssertNoPackage(factory, 2048);
        Assert.Equal(0, BasisCount(factory));

        // Hold both investigation slots, then submit from a fresh session: rejected with no investigation result.
        var second = AddPresenterSession(factory, "presenter-2");
        var held = new[] { limits.TryAdmitInvestigation(), limits.TryAdmitInvestigation() };
        Assert.All(held, slot => Assert.NotNull(slot));

        using var investigationRejected = await PostPackageAsync(client, second, size: 3072);
        Assert.Equal(HttpStatusCode.TooManyRequests, investigationRejected.StatusCode);
        var investigationBody = await investigationRejected.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("investigation concurrency limit exceeded", investigationBody.GetProperty("error").GetString());
        Assert.False(investigationBody.TryGetProperty("packageId", out _));
        AssertNoPackage(factory, 3072);
        AssertNoInvestigationResult(factory, FakePdf(3072));
        Assert.Equal(0, BasisCount(factory));

        Assert.Equal(
            new[] { WorkloadAdmissionLimits.PackageKind, WorkloadAdmissionLimits.InvestigationKind },
            limits.Events.Select(e => e.Kind).ToArray());
        Assert.Equal(
            new[] { WorkloadAdmissionLimits.PackageLimitError, WorkloadAdmissionLimits.InvestigationLimitError },
            limits.Events.Select(e => e.Error).ToArray());

        foreach (var slot in held) slot?.Dispose();

        // Once the held slots are released, the same submission is admitted.
        using var retried = await PostPackageAsync(client, second, size: 3072);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, retried.StatusCode);
    }

    [Fact]
    public void Refusals_are_recorded_up_to_a_fixed_bound()
    {
        var limits = new WorkloadAdmissionLimits(TimeProvider.System);
        using var first = limits.TryAdmitInvestigation();
        using var second = limits.TryAdmitInvestigation();

        for (var i = 0; i < WorkloadAdmissionLimits.MaxRecordedEvents + 50; i++)
        {
            Assert.Null(limits.TryAdmitInvestigation());
        }

        Assert.Equal(WorkloadAdmissionLimits.MaxRecordedEvents, limits.Events.Count);
    }
}

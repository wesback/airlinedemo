using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AirlineDemo.Api.Admission;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Evidence;
using AirlineDemo.Api.Evidence.PdfWorker;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AirlineDemo.Api.Tests;

public sealed class WorkloadAdmissionLimitsTests
{
    private const string Passcode = "test-passcode";
    private const string Case = "case-001";
    private const string SessionCookieName = "__Host-demo-session";

    private sealed record Session(string Cookie, string Csrf);

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private static WebApplicationFactory<Program> CreateFactory(WorkloadAdmissionLimits limits) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["Demo:Passcode"] = Passcode }));
            builder.ConfigureTestServices(services => services.AddSingleton<Microsoft.Extensions.AI.IChatClient>(new CountingChatClient()));
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

    private static Task<HttpResponseMessage> PostPackageAsync(HttpClient client, Session session, string? forgedUserId = null, int size = 1024)
    {
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(FakePdf(size));
        part.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(part, EvidenceEndpoints.FormFieldName, "statement.pdf");
        if (forgedUserId is not null) form.Add(new StringContent(forgedUserId), "userId");

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/cases/{Case}/packages") { Content = form };
        request.Headers.Add("Cookie", session.Cookie);
        request.Headers.Add("X-CSRF-Token", session.Csrf);
        return client.SendAsync(request);
    }

    // Reads the same stores the endpoint writes to: a rejected submission must leave none of this state behind.
    private static void AssertNoPackage(WebApplicationFactory<Program> factory, int size) =>
        Assert.Null(factory.Services.GetRequiredService<EvidencePackageStore>()
            .Find(Case, EvidenceEndpoints.PackageIdFor(Case, [new EvidenceDocument("statement.pdf", FakePdf(size))])));

    private static int BasisCount(WebApplicationFactory<Program> factory) =>
        factory.Services.GetRequiredService<EvidenceBasisStore>().History($"{Case}/statement.pdf").Count;

    private static void AssertBothInvestigationSlotsAvailable(WorkloadAdmissionLimits limits)
    {
        using var first = limits.TryAdmitInvestigation();
        using var second = limits.TryAdmitInvestigation();
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Null(limits.TryAdmitInvestigation());
    }

    [Fact]
    public async Task Package_rate_limit_uses_authenticated_session_and_rolling_window()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var limits = new WorkloadAdmissionLimits(clock);
        using var factory = CreateFactory(limits);
        using var client = factory.CreateClient();
        var presenter = await SignInAsync(client, "presenter");
        var second = AddPresenterSession(factory, "presenter-2");

        // The forged multipart userId names the other session; it must not change this caller's quota.
        for (var i = 0; i < WorkloadAdmissionLimits.PackageAttemptsPerWindow; i++)
        {
            using var admitted = await PostPackageAsync(client, presenter, forgedUserId: "presenter-2", size: 1024 + i);
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
        using var factory = CreateFactory(limits);
        using var client = factory.CreateClient();
        var presenter = await SignInAsync(client, "presenter");
        var admitted = new CountdownEvent(WorkloadAdmissionLimits.MaxConcurrentInvestigations);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holders = Enumerable.Range(0, WorkloadAdmissionLimits.MaxConcurrentInvestigations).Select(_ => Task.Run(async () =>
        {
            using var slot = limits.TryAdmitInvestigation() ?? throw new InvalidOperationException("slot refused");
            admitted.Signal();
            await release.Task;
        })).ToArray();
        await Task.Run(() => admitted.Wait());

        Assert.Null(limits.TryAdmitInvestigation());
        var refusal = Assert.Single(limits.Events);
        Assert.Equal(WorkloadAdmissionLimits.InvestigationKind, refusal.Kind);
        Assert.Equal(WorkloadAdmissionLimits.InvestigationLimitError, refusal.Error);

        // The endpoint shares this limiter: a submission while both slots are busy is refused before any case, package or result exists.
        using var refused = await PostPackageAsync(client, presenter);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        var refusedBody = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(WorkloadAdmissionLimits.InvestigationLimitError, refusedBody.GetProperty("error").GetString());
        Assert.Equal(2, limits.Events.Count);
        AssertNoPackage(factory, 1024);
        Assert.Equal(0, BasisCount(factory));

        // Success: both admitted investigations complete and release their slots.
        release.SetResult();
        await Task.WhenAll(holders);
        AssertBothInvestigationSlotsAvailable(limits);

        // Cancellation: the slot is released when the cancelled investigation exits.
        using var cts = new CancellationTokenSource();
        var cancelled = Task.Run(async () =>
        {
            using var slot = limits.TryAdmitInvestigation() ?? throw new InvalidOperationException("slot refused");
            await Task.Delay(Timeout.Infinite, cts.Token);
        });
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        AssertBothInvestigationSlotsAvailable(limits);

        // Exception: the slot is released when the investigation throws.
        var faulted = Task.Run(() =>
        {
            using var slot = limits.TryAdmitInvestigation() ?? throw new InvalidOperationException("slot refused");
            throw new InvalidOperationException("boom");
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => faulted);
        AssertBothInvestigationSlotsAvailable(limits);

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

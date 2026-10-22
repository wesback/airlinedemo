using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Diagnostics;
using AirlineDemo.Api.Evidence;
using AirlineDemo.Api.Fixtures;
using AirlineDemo.Api.Finance;
using AirlineDemo.Api.Investigation;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Projection;
using AirlineDemo.Api.Requests;
using AirlineDemo.Api.Reviews;
using AirlineDemo.Api.Runs;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AirlineDemo.Api.Tests;

public sealed class CasePackDiagnosticsTests
{
    private sealed class CaptureLogger : ILogger
    {
        private readonly Lock _gate = new();
        private readonly List<IReadOnlyDictionary<string, object?>> _events = [];
        public IReadOnlyList<IReadOnlyDictionary<string, object?>> Events { get { lock (_gate) return _events.ToArray(); } }
        public int Count { get { lock (_gate) return _events.Count; } }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                var snapshot = values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                lock (_gate) _events.Add(snapshot);
            }
        }
    }

    [Fact]
    public async Task AC022_OperationAndOutcomeTableEmitsOnlyApprovedFields()
    {
        var logger = new CaptureLogger();
        var expectedEvents = new[] { "pack_load", "case_list", "scene_projection", "reset" };
        var expectedOutcomes = new[] { "success", "failed", "validation_failed", "denied", "invalid", "conflict", "replayed", "rate_limited" };
        Assert.Equal(expectedEvents.ToHashSet(), CasePackDiagnostics.AllowedEventTypes);
        Assert.Equal(expectedOutcomes.ToHashSet(), CasePackDiagnostics.AllowedOutcomes);

        IReadOnlyDictionary<string, object?>[] emissions =
        [
            .. expectedOutcomes.Select(outcome => CasePackDiagnostics.PackLoad(logger, outcome, 1, "pack-1.0")),
            .. expectedOutcomes.Select(outcome => CasePackDiagnostics.CaseList(logger, outcome, 2)),
            .. expectedOutcomes.Select(outcome => CasePackDiagnostics.SceneProjection(logger, outcome, 3, "cp-case-c")),
            .. expectedOutcomes.Select(outcome => CasePackDiagnostics.Reset(logger, outcome, 4, "cp-case-c", "cmd-1")),
        ];

        Assert.Equal(emissions.Length, logger.Events.Count);
        for (var index = 0; index < emissions.Length; index++)
        {
            var fields = emissions[index];
            var eventIndex = index / expectedOutcomes.Length;
            var outcome = expectedOutcomes[index % expectedOutcomes.Length];
            var eventType = expectedEvents[eventIndex];
            var expectedKeys = new HashSet<string>(StringComparer.Ordinal)
            {
                "contractVersion", "outcome", "durationMs", "eventType"
            };
            if (eventType is "scene_projection" or "reset") expectedKeys.Add("caseId");
            if (eventType == "reset") expectedKeys.Add("commandId");
            if (eventType == "pack_load") expectedKeys.Add("packVersion");

            Assert.Equal(expectedKeys.Order(), fields.Keys.Order());
            Assert.Equal(eventType, fields["eventType"]);
            Assert.Equal(outcome, fields["outcome"]);
            Assert.Equal(1, fields["contractVersion"]);
            Assert.True(fields["durationMs"] is long duration && duration >= 0);
            Assert.True(fields.Keys.All(CasePackDiagnostics.ApprovedFields.Contains));
        }

        var withoutIdentifiers = CasePackDiagnostics.CaseList(logger, "success", 0);
        Assert.DoesNotContain("caseId", withoutIdentifiers.Keys);
        Assert.DoesNotContain("commandId", withoutIdentifiers.Keys);
        Assert.DoesNotContain("packVersion", withoutIdentifiers.Keys);

        var countBeforeInvalidEvent = logger.Count;
        Assert.Throws<ArgumentException>(() => CasePackDiagnostics.Emit(logger, "other", "success", 1));
        Assert.Equal(countBeforeInvalidEvent, logger.Count);
        var countBeforeInvalidOutcome = logger.Count;
        Assert.Throws<ArgumentException>(() => CasePackDiagnostics.Emit(logger, "reset", "other", 1));
        Assert.Equal(countBeforeInvalidOutcome, logger.Count);

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Demo:Passcode"] = "operation-path-passcode",
                ["Persistence:EvidenceProvider"] = "Memory",
            })).ConfigureServices(services => services.AddSingleton<ILoggerProvider>(new CaptureLoggerProvider(logger))));
        using var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;
        var operationStore = factory.Services.GetRequiredService<ICaseStateStore>();
        if (operationStore.OwnerOf("cp-case-c") is null)
            operationStore.CreateCase("cp-case-c", "operation-owner");
        Assert.Contains(logger.Events, fields => fields.TryGetValue("eventType", out var eventType)
            && Equals(eventType, "pack_load")
            && fields.Keys.Order().SequenceEqual(new[] { "contractVersion", "durationMs", "eventType", "outcome", "packVersion" }.Order()));
        var sessions = factory.Services.GetRequiredService<DemoSessionStore>();
        var presenter = sessions.Create(new DemoUser("operation-presenter", "Operation Presenter", "presenter", ["cp-case-c"]));
        var resetPresenter = sessions.Create(new DemoUser("operation-reset-presenter", "Operation Reset Presenter", "presenter", ["cp-case-c"]));
        var resetBase = new { contractVersion = 1, variant = "blocked" };
        async Task<(HttpStatusCode Status, string Body)> SendReset(string commandId, long? expectedRevision = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/cases/cp-case-c/reset-blocked-variant");
            request.Headers.Add("Cookie", $"{DemoAuthEndpoints.CookieName}={resetPresenter.Id}");
            request.Headers.Add(DemoAuthEndpoints.CsrfHeaderName, resetPresenter.CsrfToken);
            var revision = expectedRevision ?? 0L;
            request.Content = JsonContent.Create(new { resetBase.contractVersion, commandId, expectedRevision = revision, resetBase.variant });
            using var response = await client.SendAsync(request, ct);
            return (response.StatusCode, await response.Content.ReadAsStringAsync(ct));
        }
        var acceptedCommand = "operation-reset-accepted-" + Guid.NewGuid().ToString("N");
        var actualOwner = operationStore.OwnerOf("cp-case-c")!;
        var acceptedPrior = operationStore.Read(operationStore.AuthorizeCase("cp-case-c", actualOwner)).Revision;
        var accepted = await SendReset(acceptedCommand, acceptedPrior);
        Assert.True(accepted.Status == HttpStatusCode.OK, $"accepted={accepted.Status}; body={accepted.Body}");
        var replayed = await SendReset(acceptedCommand, acceptedPrior);
        Assert.True(replayed.Status == HttpStatusCode.OK, $"replayed={replayed.Status}; body={replayed.Body}");
        var currentOwner = operationStore.OwnerOf("cp-case-c")!;
        var currentScope = operationStore.AuthorizeCase("cp-case-c", currentOwner);
        var currentRevision = operationStore.Read(currentScope).Revision;
        var allowedBeforeLimit = await SendReset("operation-reset-before-limit-" + Guid.NewGuid().ToString("N"), currentRevision);
        Assert.True(allowedBeforeLimit.Status == HttpStatusCode.OK, $"before-limit={allowedBeforeLimit.Status}; body={allowedBeforeLimit.Body}");
        var limitedRevision = operationStore.Read(currentScope).Revision;
        var limited = await SendReset("operation-reset-rate-limited-" + Guid.NewGuid().ToString("N"), limitedRevision);
        Assert.True(limited.Status == HttpStatusCode.TooManyRequests, $"limited={limited.Status}; body={limited.Body}");
        Assert.Contains(logger.Events, fields => Equals(fields.GetValueOrDefault("eventType"), "reset") && Equals(fields.GetValueOrDefault("outcome"), "success"));
        Assert.Contains(logger.Events, fields => Equals(fields.GetValueOrDefault("eventType"), "reset") && Equals(fields.GetValueOrDefault("outcome"), "replayed"));
        Assert.Contains(logger.Events, fields => Equals(fields.GetValueOrDefault("eventType"), "reset") && Equals(fields.GetValueOrDefault("outcome"), "rate_limited"));

        using var listRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/cases");
        listRequest.Headers.Add("Cookie", $"{DemoAuthEndpoints.CookieName}={presenter.Id}");
        using var listResponse = await client.SendAsync(listRequest, ct);
        using var sceneRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/cases/cp-case-c/scene");
        sceneRequest.Headers.Add("Cookie", $"{DemoAuthEndpoints.CookieName}={presenter.Id}");
        using var sceneResponse = await client.SendAsync(sceneRequest, ct);
        using var deniedResetRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/cases/cp-case-c/reset-blocked-variant");
        deniedResetRequest.Headers.Add("Cookie", $"{DemoAuthEndpoints.CookieName}={presenter.Id}");
        deniedResetRequest.Headers.Add(DemoAuthEndpoints.CsrfHeaderName, presenter.CsrfToken);
        deniedResetRequest.Content = JsonContent.Create(new { contractVersion = 2, commandId = "operation-reset-1", expectedRevision = 0L, variant = "blocked" });
        using var deniedReset = await client.SendAsync(deniedResetRequest, ct);
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        Assert.Contains(logger.Events, fields => fields.TryGetValue("eventType", out var value) && Equals(value, "scene_projection"));
        Assert.Equal(HttpStatusCode.BadRequest, deniedReset.StatusCode);
        Assert.Contains(logger.Events, fields => fields.TryGetValue("eventType", out var value) && Equals(value, "case_list"));
        Assert.Contains(logger.Events, fields => fields.TryGetValue("eventType", out var value) && Equals(value, "reset") && fields.TryGetValue("outcome", out var outcome) && Equals(outcome, "invalid"));
    }

    [Fact]
    public async Task AC022_RuntimeOperationsEmitIsolatedDiagnostics()
    {
        var logger = new CaptureLogger();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Demo:Passcode"] = "runtime-diagnostics-passcode",
                ["Persistence:EvidenceProvider"] = "Memory",
            })).ConfigureServices(services =>
            {
                services.AddSingleton<ILoggerProvider>(new CaptureLoggerProvider(logger));
                services.RemoveAll<IArrCaseHistorySource>();
                services.AddSingleton<IArrCaseHistorySource>(sp => new RuntimeArrHistorySource(sp.GetRequiredService<ICaseStateStore>()));
            }));
        using var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        IReadOnlyDictionary<string, object?>[] AddedEvents(Action operation)
        {
            var before = logger.Count;
            operation();
            return logger.Events.Skip(before).Where(IsCasePackDiagnostic).ToArray();
        }
        async Task<IReadOnlyDictionary<string, object?>[]> AddedEventsAsync(Func<Task> operation)
        {
            var before = logger.Count;
            await operation();
            return logger.Events.Skip(before).Where(IsCasePackDiagnostic).ToArray();
        }

        var publisher = factory.Services.GetRequiredService<AircraftCasePackPublisher>();
        var configuredLoader = factory.Services.GetRequiredService<AircraftCasePackLoader>();
        var successEvents = await AddedEventsAsync(() => publisher.PublishAsync(configuredLoader, 0));
        Assert.Single(successEvents);
        Assert.Equal(new HashSet<string> { "contractVersion", "outcome", "durationMs", "eventType", "packVersion" }, successEvents[0].Keys.ToHashSet());
        Assert.Equal("pack_load", successEvents[0]["eventType"]);
        Assert.Equal("success", successEvents[0]["outcome"]);
        Assert.Equal(1, successEvents[0]["contractVersion"]);
        Assert.True(successEvents[0]["durationMs"] is long duration && duration >= 0);

        var emptyDirectory = Path.Combine(Path.GetTempPath(), "airlinedemo-empty-pack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyDirectory);
        try
        {
            var failedEvents = await AddedEventsAsync(() => Assert.ThrowsAsync<FileNotFoundException>(() => publisher.PublishAsync(new AircraftCasePackLoader(emptyDirectory), 0)));
            Assert.Single(failedEvents);
            Assert.Equal(new HashSet<string> { "contractVersion", "outcome", "durationMs", "eventType" }, failedEvents[0].Keys.ToHashSet());
            Assert.Equal("pack_load", failedEvents[0]["eventType"]);
            Assert.Equal("validation_failed", failedEvents[0]["outcome"]);
        }
        finally
        {
            Directory.Delete(emptyDirectory, recursive: true);
        }

        var store = factory.Services.GetRequiredService<ICaseStateStore>();
        if (store.OwnerOf("cp-case-c") is null) store.CreateCase("cp-case-c", "runtime-owner");
        var sessions = factory.Services.GetRequiredService<DemoSessionStore>();
        var presenter = sessions.Create(new DemoUser("runtime-presenter", "Runtime Presenter", "presenter", ["cp-case-c"]));

        using var listRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/cases");
        listRequest.Headers.Add("Cookie", $"{DemoAuthEndpoints.CookieName}={presenter.Id}");
        var listEvents = await AddedEventsAsync(async () => { using var response = await client.SendAsync(listRequest, ct); Assert.Equal(HttpStatusCode.OK, response.StatusCode); });
        Assert.Single(listEvents);
        Assert.Equal(new HashSet<string> { "contractVersion", "outcome", "durationMs", "eventType" }, listEvents[0].Keys.ToHashSet());
        Assert.Equal("case_list", listEvents[0]["eventType"]);
        Assert.Equal("success", listEvents[0]["outcome"]);

        var sceneOwner = store.OwnerOf("cp-case-c")!;
        var sceneScope = store.AuthorizeCase("cp-case-c", sceneOwner);
        var sceneState = store.Read(sceneScope);
        var at = new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero);
        if (sceneState.Basis.Count == 0)
        {
            store.Commit(sceneScope, sceneState.Revision, new CaseCommit(
                CaseStateStore.CreateBasisVersion(1, "runtime basis", string.Empty), null,
                "runtime-owner", "basis", "runtime scene basis", At: at));
        }
        sceneState = store.Read(sceneScope);
        if ((sceneState.JourneyEvents?.Count ?? 0) == 0)
        {
            store.Commit(sceneScope, sceneState.Revision, new CaseCommit(null, null, "runtime-owner", "journey", "runtime scene history", At: at,
                JourneyEvent: new CaseJourneyEventDraft("runtime-event-1", "runtime-run", "cp-case-c", "evidence-reviewed", "basis-v1", 1,
                    "presenter", "Runtime scene history", "AI agent", [])));
        }
        var packages = factory.Services.GetRequiredService<EvidencePackageStore>();
        packages.Submit("runtime-package", "cp-case-c", () => new EvidencePackageOutcome("complete",
            [new EvidenceDocumentResult("aircraft-summary.txt", "accepted", "accepted", 1, null)]));

        var direct = CasePackSceneProjection.BuildResult(store, sceneScope, projectionSnapshot, "presenter", packages, null);
        Assert.True(direct.Succeeded, $"scene build failed: {direct.ErrorCode}; payload={direct.PayloadLimit}");

        var projectionSnapshot = factory.Services.GetRequiredService<IAircraftCasePackSnapshotStore>().Read()!;
        var runtimeHistory = factory.Services.GetRequiredService<IArrCaseHistorySource>();
        using var sceneRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/cases/cp-case-c/scene");
        sceneRequest.Headers.Add("Cookie", $"{DemoAuthEndpoints.CookieName}={presenter.Id}");
        var sceneEvents = await AddedEventsAsync(async () => { using var response = await client.SendAsync(sceneRequest, ct); Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct)); });
        Assert.Single(sceneEvents);
        Assert.Equal(new HashSet<string> { "contractVersion", "outcome", "durationMs", "eventType", "caseId" }, sceneEvents[0].Keys.ToHashSet());
        Assert.Equal("scene_projection", sceneEvents[0]["eventType"]);
        Assert.Equal("success", sceneEvents[0]["outcome"]);
        Assert.Equal("cp-case-c", sceneEvents[0]["caseId"]);

        async Task<(HttpStatusCode Status, IReadOnlyDictionary<string, object?>[] Events)> SendReset(string commandId, string variant, long revision)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/cases/cp-case-c/reset-blocked-variant");
            request.Headers.Add("Cookie", $"{DemoAuthEndpoints.CookieName}={presenter.Id}");
            request.Headers.Add(DemoAuthEndpoints.CsrfHeaderName, presenter.CsrfToken);
            request.Content = JsonContent.Create(new { contractVersion = 1, commandId, expectedRevision = revision, variant });
            return await SendResetWithResponse(request);
        }
        async Task<(HttpStatusCode Status, IReadOnlyDictionary<string, object?>[] Events)> SendResetWithResponse(HttpRequestMessage request)
        {
            var before = logger.Count;
            using var response = await client.SendAsync(request, ct);
            return (response.StatusCode, logger.Events.Skip(before).Where(IsCasePackDiagnostic).ToArray());
        }

        var owner = store.OwnerOf("cp-case-c")!;
        var scope = store.AuthorizeCase("cp-case-c", owner);
        var firstRevision = store.Read(scope).Revision;
        var acceptedCommand = "runtime-reset-accepted";
        var accepted = await SendReset(acceptedCommand, "blocked", firstRevision);
        Assert.Equal(HttpStatusCode.OK, accepted.Status);
        Assert.Single(accepted.Events);
        Assert.Equal(new HashSet<string> { "contractVersion", "outcome", "durationMs", "eventType", "caseId", "commandId" }, accepted.Events[0].Keys.ToHashSet());
        Assert.Equal("reset", accepted.Events[0]["eventType"]);
        Assert.Equal("success", accepted.Events[0]["outcome"]);
        Assert.Equal("cp-case-c", accepted.Events[0]["caseId"]);
        Assert.Equal(acceptedCommand, accepted.Events[0]["commandId"]);

        var replayed = await SendReset(acceptedCommand, "blocked", firstRevision);
        Assert.Equal(HttpStatusCode.OK, replayed.Status);
        Assert.Single(replayed.Events);
        Assert.Equal("reset", replayed.Events[0]["eventType"]);
        Assert.Equal("replayed", replayed.Events[0]["outcome"]);
        Assert.Equal(new HashSet<string> { "contractVersion", "outcome", "durationMs", "eventType", "caseId", "commandId" }, replayed.Events[0].Keys.ToHashSet());

        var currentRevision = store.Read(scope).Revision;
        var allowed = await SendReset("runtime-reset-third", "blocked", currentRevision);
        Assert.Equal(HttpStatusCode.OK, allowed.Status);
        var limited = await SendReset("runtime-reset-limited", "blocked", store.Read(scope).Revision);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.Status);
        Assert.Single(limited.Events);
        Assert.Equal("reset", limited.Events[0]["eventType"]);
        Assert.Equal("rate_limited", limited.Events[0]["outcome"]);
        Assert.Equal(new HashSet<string> { "contractVersion", "outcome", "durationMs", "eventType", "caseId" }, limited.Events[0].Keys.ToHashSet());

        var invalidPresenter = sessions.Create(new DemoUser("runtime-invalid-presenter", "Runtime Invalid Presenter", "presenter", ["cp-case-c"]));
        using var invalidRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/cases/cp-case-c/reset-blocked-variant");
        invalidRequest.Headers.Add("Cookie", $"{DemoAuthEndpoints.CookieName}={invalidPresenter.Id}");
        invalidRequest.Headers.Add(DemoAuthEndpoints.CsrfHeaderName, invalidPresenter.CsrfToken);
        invalidRequest.Content = JsonContent.Create(new { contractVersion = 1, commandId = "runtime-reset-invalid", expectedRevision = store.Read(scope).Revision, variant = "unsupported" });
        var beforeInvalid = logger.Count;
        using var invalidResponse = await client.SendAsync(invalidRequest, ct);
        var invalidEvents = logger.Events.Skip(beforeInvalid).Where(IsCasePackDiagnostic).ToArray();
        var invalidStatus = invalidResponse.StatusCode;
        var invalid = (Status: invalidStatus, Events: invalidEvents);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.Status);
        Assert.Single(invalid.Events);
        Assert.Equal("reset", invalid.Events[0]["eventType"]);
        Assert.Equal("invalid", invalid.Events[0]["outcome"]);
        Assert.Equal(new HashSet<string> { "contractVersion", "outcome", "durationMs", "eventType", "caseId", "commandId" }, invalid.Events[0].Keys.ToHashSet());
    }

    [Fact]
    public async Task AC022_SecretAndContentCanariesNeverReachDiagnostics()
    {
        var propertyNames = new[] { "documentText", "prompt", "response", "cookie", "passcode", "token", "authorization", "connectionString", "credential", "auditDetail" };
        var canaries = propertyNames.Select((name, index) => $"{name}-canary-{index}").ToArray();
        var injected = propertyNames.Zip(canaries, (name, value) => new KeyValuePair<string, object?>($"{name}-canary-property", value));
        var logger = new CaptureLogger();
        var fields = CasePackDiagnostics.Reset(logger, "success", 1, "cp-case-c", "command-1");

        foreach (var propertyName in propertyNames)
        {
            var redacted = TelemetryRedaction.Redact([new KeyValuePair<string, object?> (propertyName, "secret-canary")]);
            Assert.Empty(redacted);
            Assert.DoesNotContain(propertyName, fields.Keys, StringComparer.OrdinalIgnoreCase);
        }
        Assert.Empty(TelemetryRedaction.Redact(injected));

        var activityTags = new List<KeyValuePair<string, string?>>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TelemetryRedaction.ServiceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => activityTags.AddRange(activity.Tags.Select(tag => new KeyValuePair<string, string?>(tag.Key, tag.Value))),
        };
        ActivitySource.AddActivityListener(listener);

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Demo:Passcode"] = "canary-passcode",
                ["Persistence:EvidenceProvider"] = "Memory",
            })).ConfigureServices(services => services.AddSingleton<ILoggerProvider>(new CaptureLoggerProvider(logger))));
        using var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;
        var caseStore = factory.Services.GetRequiredService<ICaseStateStore>();
        if (caseStore.OwnerOf("cp-case-c") is null)
            caseStore.CreateCase("cp-case-c", "canary-owner");
        var sessions = factory.Services.GetRequiredService<DemoSessionStore>();
        var presenter = sessions.Create(new DemoUser("canary-presenter", "Canary Presenter", "presenter", ["cp-case-c"]));
        using var resetRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/cases/cp-case-c/reset-blocked-variant");
        resetRequest.Headers.Add("Cookie", $"{DemoAuthEndpoints.CookieName}={presenter.Id}");
        resetRequest.Headers.Add(DemoAuthEndpoints.CsrfHeaderName, presenter.CsrfToken);
        resetRequest.Content = JsonContent.Create(new Dictionary<string, object?>
        {
            ["contractVersion"] = 1,
            ["commandId"] = "canary-command-1",
            ["expectedRevision"] = 0L,
            ["variant"] = "blocked",
            ["documentText"] = canaries[0], ["prompt"] = canaries[1], ["response"] = canaries[2],
            ["cookie"] = canaries[3], ["passcode"] = canaries[4], ["token"] = canaries[5],
            ["authorization"] = canaries[6], ["connectionString"] = canaries[7],
            ["credential"] = canaries[8], ["auditDetail"] = canaries[9],
        });
        using var resetResponse = await client.SendAsync(resetRequest, ct);
        Assert.Equal(HttpStatusCode.BadRequest, resetResponse.StatusCode);

        Assert.DoesNotContain(activityTags, tag => propertyNames.Contains(tag.Key, StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain(activityTags.Select(tag => tag.Value), value => canaries.Contains(value));
        Assert.DoesNotContain(fields.Values, value => value is string text && canaries.Contains(text));
        Assert.DoesNotContain(logger.Events.SelectMany(e => e.Keys), key => propertyNames.Any(name => key.Contains(name, StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain(logger.Events.SelectMany(e => e.Values).OfType<string>(), value => canaries.Contains(value));
    }

    [Fact]
    public async Task NFR006_DiagnosticsStaySeparateFromAuditAndHaveNoIngestRoute()
    {
        var logger = new CaptureLogger();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Demo:Passcode"] = "nfr006-passcode",
                ["Persistence:EvidenceProvider"] = "Memory",
            }));
            builder.ConfigureServices(services => services.AddSingleton<ILoggerProvider>(new CaptureLoggerProvider(logger)));
        });
        using var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;
        var caseStore = factory.Services.GetRequiredService<AirlineDemo.Api.Persistence.ICaseStateStore>();
        if (caseStore.OwnerOf("cp-case-c") is null)
            caseStore.CreateCase("cp-case-c", "nfr006-owner");
        var nfrOwner = caseStore.OwnerOf("cp-case-c")!;
        var auditScope = caseStore.AuthorizeCase("cp-case-c", nfrOwner);
        var auditCountBefore = caseStore.ReadAudit(auditScope).Count;

        using var response = await client.PostAsJsonAsync("/api/v1/telemetry", new
        {
            documentText = "document-canary",
            prompt = "prompt-canary",
            response = "response-canary",
            cookie = "cookie-canary",
            passcode = "passcode-canary",
            token = "token-canary",
            authorization = "authorization-canary",
            connectionString = "connection-string-canary",
            credential = "credential-canary",
            auditDetail = "audit-detail-canary",
        }, ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var auditCountAfter = caseStore.ReadAudit(auditScope).Count;
        Assert.Equal(auditCountBefore, auditCountAfter);

        var sessions = factory.Services.GetRequiredService<DemoSessionStore>();
        var presenter = sessions.Create(new DemoUser("nfr006-presenter", "NFR Presenter", "presenter", ["cp-case-c"]));
        async Task<HttpStatusCode> SendAppReset(string commandId, string variant)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/cases/cp-case-c/reset-blocked-variant");
            request.Headers.Add("Cookie", $"{DemoAuthEndpoints.CookieName}={presenter.Id}");
            request.Headers.Add(DemoAuthEndpoints.CsrfHeaderName, presenter.CsrfToken);
            request.Content = JsonContent.Create(new { contractVersion = 1, commandId, expectedRevision = 0L, variant });
            using var response = await client.SendAsync(request, ct);
            return response.StatusCode;
        }
        var appCommand = "nfr006-app-reset";
        Assert.Equal(HttpStatusCode.OK, await SendAppReset(appCommand, "blocked"));
        var auditAfterAcceptedReset = caseStore.ReadAudit(caseStore.AuthorizeCase("cp-case-c", nfrOwner)).Count;
        Assert.Equal(HttpStatusCode.OK, await SendAppReset(appCommand, "blocked"));
        var auditAfterAppReplay = caseStore.ReadAudit(caseStore.AuthorizeCase("cp-case-c", nfrOwner)).Count;
        Assert.Equal(auditAfterAcceptedReset, auditAfterAppReplay);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await SendAppReset("nfr006-app-rejected", "unsupported"));
        Assert.Equal(auditAfterAppReplay, caseStore.ReadAudit(caseStore.AuthorizeCase("cp-case-c", nfrOwner)).Count);

        var resetScope = caseStore.AuthorizeCase("cp-case-c", nfrOwner);
        var resetBeforeReplay = caseStore.ReadAudit(resetScope).Count;
        var replayCommandId = "nfr006-replay-" + Guid.NewGuid().ToString("N");
        var resetResult = caseStore.CommitBlockedVariantReset(resetScope, caseStore.Read(resetScope).Revision, "nfr006-owner", replayCommandId, "blocked");
        var auditCountAfterAcceptedReset = caseStore.ReadAudit(resetScope).Count;
        Assert.Equal(resetBeforeReplay + 1, auditCountAfterAcceptedReset);
        var replayResult = caseStore.CommitBlockedVariantReset(resetScope, resetResult.Receipt.PriorRevision, "nfr006-owner", replayCommandId, "blocked");
        Assert.True(replayResult.Replayed);
        Assert.Equal(auditCountAfterAcceptedReset, caseStore.ReadAudit(resetScope).Count);

        var rejectedCountBefore = caseStore.ReadAudit(resetScope).Count;
        Assert.Throws<ArgumentException>(() => caseStore.CommitBlockedVariantReset(resetScope, caseStore.Read(resetScope).Revision, "nfr006-owner", "nfr006-rejected-command", "unsupported"));
        Assert.Equal(rejectedCountBefore, caseStore.ReadAudit(resetScope).Count);

        Assert.DoesNotContain(logger.Events.SelectMany(e => e.Keys), key => key.Equals("auditDetail", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(logger.Events.SelectMany(e => e.Values).OfType<string>(), value => value.Contains("canary", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsCasePackDiagnostic(IReadOnlyDictionary<string, object?> fields) =>
        fields.TryGetValue("eventType", out var eventType)
        && eventType is string type
        && CasePackDiagnostics.AllowedEventTypes.Contains(type);

    private sealed class RuntimeArrHistorySource(ICaseStateStore store) : IArrCaseHistorySource
    {
        public ArrCaseHistorySelection? Read(string caseId, string runId, string basisId)
        {
            var owner = store.OwnerOf(caseId);
            if (owner is null) return null;
            var state = store.Read(store.AuthorizeCase(caseId, owner));
            var events = (state.JourneyEvents ?? [])
                .Where(item => item.CaseId == caseId && item.RunId == runId && item.BasisId == basisId)
                .OrderBy(item => item.Sequence).ToArray();
            return events.Length == 0 ? null : new ArrCaseHistorySelection(caseId, runId, basisId, events, false);
        }
    }

    private sealed class CaptureLoggerProvider(CaptureLogger logger) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => logger;
        public void Dispose() { }
    }
}

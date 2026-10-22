using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Diagnostics;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Runs;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AirlineDemo.Api.Tests;

public sealed class CasePackDiagnosticsTests
{
    private sealed class CaptureLogger : ILogger
    {
        public List<IReadOnlyDictionary<string, object?>> Events { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
                Events.Add(values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
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
        var auditScope = caseStore.AuthorizeCase("cp-case-c", "nfr006-owner");
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
        var auditAfterAcceptedReset = caseStore.ReadAudit(caseStore.AuthorizeCase("cp-case-c", "nfr006-owner")).Count;
        Assert.Equal(HttpStatusCode.OK, await SendAppReset(appCommand, "blocked"));
        var auditAfterAppReplay = caseStore.ReadAudit(caseStore.AuthorizeCase("cp-case-c", "nfr006-owner")).Count;
        Assert.Equal(auditAfterAcceptedReset, auditAfterAppReplay);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await SendAppReset("nfr006-app-rejected", "unsupported"));
        Assert.Equal(auditAfterAppReplay, caseStore.ReadAudit(caseStore.AuthorizeCase("cp-case-c", "nfr006-owner")).Count);

        var resetScope = caseStore.AuthorizeCase("cp-case-c", "nfr006-owner");
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

    private sealed class CaptureLoggerProvider(CaptureLogger logger) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => logger;
        public void Dispose() { }
    }
}

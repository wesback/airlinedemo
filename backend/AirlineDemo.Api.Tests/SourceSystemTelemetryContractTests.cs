using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Diagnostics;
using AirlineDemo.Api.Investigation;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Requests;
using AirlineDemo.Api.SourceSystems;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;

namespace AirlineDemo.Api.Tests;

public sealed class SourceSystemTelemetryContractTests
{
    private const string CaseId = "source-telemetry-case";
    private const string OtherCaseId = "source-telemetry-other";
    private const string PayloadCanary = "SOURCE-PAYLOAD-CANARY";
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory(string prefix) => Path = Directory.CreateTempSubdirectory(prefix).FullName;

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class ControlledSource : ISourceSystem<TechnicalPayload>
    {
        public bool Unavailable { get; set; }
        public bool WaitUntilCancelled { get; set; }
        public int RecordCount { get; set; } = 1;

        public async Task<SourceReadResult<IReadOnlyList<SourceRecord<TechnicalPayload>>>> ListAsync(
            string caseId, CancellationToken cancellationToken = default, int? maxRecords = null)
        {
            if (WaitUntilCancelled) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (Unavailable) return new SourceUnavailable<IReadOnlyList<SourceRecord<TechnicalPayload>>>("technical");
            return new SourceReadSucceeded<IReadOnlyList<SourceRecord<TechnicalPayload>>>(
                Enumerable.Range(1, RecordCount).Select(index => new SourceRecord<TechnicalPayload>
                {
                    SourceId = "technical",
                    RecordId = $"record-{index}",
                    RecordVersion = 1,
                    AsOf = new DateOnly(2026, 10, 1),
                    CaseId = caseId,
                    Kind = "telemetry-test",
                    Payload = new TechnicalPayload(new Dictionary<string, string> { ["value"] = PayloadCanary }),
                }).ToArray());
        }

        public Task<SourceReadResult<SourceRecord<TechnicalPayload>>> GetAsync(
            string caseId, string recordId, CancellationToken cancellationToken = default) =>
            Task.FromResult<SourceReadResult<SourceRecord<TechnicalPayload>>>(new SourceRecordNotFound<SourceRecord<TechnicalPayload>>(recordId));
    }

    private sealed class ControlledAdmission : ISourceReadAdmissionStore
    {
        public bool Reject { get; set; }

        public Task<Guid?> TryAcquireAsync(string sessionId, string caseId, DateTimeOffset requestedAt, CancellationToken cancellationToken) =>
            Task.FromResult(Reject ? (Guid?)null : Guid.NewGuid());

        public Task<bool> ReleaseAsync(Guid leaseId, DateTimeOffset releasedAt, CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }

    private sealed class AuditFailingCaseStore(ICaseStateStore inner) : ICaseStateStore
    {
        public bool FailAudit { get; set; } = true;

        public CaseScope AuthorizeCase(string caseId, string owner) => inner.AuthorizeCase(caseId, owner);
        public string? OwnerOf(string caseId) => inner.OwnerOf(caseId);
        public CaseScope CreateCase(string caseId, string owner) => inner.CreateCase(caseId, owner);
        public CaseState Read(CaseScope scope) => inner.Read(scope);
        public BasisVersion GetBasisVersion(CaseScope scope, int version) => inner.GetBasisVersion(scope, version);
        public IReadOnlyList<AuditEvent> ReadAudit(CaseScope scope) => inner.ReadAudit(scope);
        public CommitResult Commit(CaseScope scope, long expectedRevision, CaseCommit commit)
        {
            if (FailAudit && commit.Action == SourceModes.Action) throw new IOException("simulated audit storage failure");
            return inner.Commit(scope, expectedRevision, commit);
        }
        public IReadOnlyList<WorkItem> ListWorkItems(CaseScope scope) => inner.ListWorkItems(scope);
        public CommitResult CreateWorkItem(CaseScope scope, long expectedRevision, WorkItemDraft draft, string actor) =>
            inner.CreateWorkItem(scope, expectedRevision, draft, actor);
        public CommitResult ChangeWorkItemState(CaseScope scope, long expectedRevision, string itemId, string state, string actor) =>
            inner.ChangeWorkItemState(scope, expectedRevision, itemId, state, actor);
        public void AppendTrace(CaseScope scope, InvestigationTrace trace) => inner.AppendTrace(scope, trace);
        public InvestigationTrace? FindTrace(CaseScope scope, string caseId, string runId, string investigationId) =>
            inner.FindTrace(scope, caseId, runId, investigationId);
    }

    private sealed record CapturedEvent(
        string Name,
        string? SeverityText,
        int SeverityNumber,
        IReadOnlyList<KeyValuePair<string, object?>> Attributes,
        string? Body,
        bool HasTimestamp,
        bool HasOtelSeverityText,
        bool HasOtelSeverityNumber,
        string? OtelSeverityText,
        int? OtelSeverityNumber,
        IReadOnlyList<KeyValuePair<string, object?>> Resource,
        string? TraceId,
        string? SpanId);

    private sealed class EventCapture(List<CapturedEvent> events) : BaseProcessor<LogRecord>
    {
        public override void OnEnd(LogRecord data)
        {
            var attributes = (data.Attributes ?? []).ToList();
            var name = attributes.FirstOrDefault(attribute => attribute.Key == "event.name").Value?.ToString() ?? string.Empty;
            var severityText = attributes.FirstOrDefault(attribute => attribute.Key == "severity.text").Value?.ToString();
            var severityNumber = Convert.ToInt32(attributes.FirstOrDefault(attribute => attribute.Key == "severity.number").Value);
            var otelSeverityText = ReadInternalLogRecordProperty(data, "SeverityText")?.ToString();
            var rawOtelSeverityNumber = ReadInternalLogRecordProperty(data, "Severity");
            var otelSeverityNumber = rawOtelSeverityNumber is null
                ? (int?)null
                : Convert.ToInt32(rawOtelSeverityNumber);
            var traceId = data.TraceId.ToHexString();
            var spanId = data.SpanId.ToHexString();
            var resource = ParentProvider?.GetType()
                .GetProperty("Resource", BindingFlags.Instance | BindingFlags.Public)?
                .GetValue(ParentProvider)
                ?? throw new InvalidOperationException("The OpenTelemetry logger provider did not expose its resource.");
            var resourceAttributes = resource.GetType()
                .GetProperty("Attributes", BindingFlags.Instance | BindingFlags.Public)?
                .GetValue(resource) as IEnumerable<KeyValuePair<string, object>>
                ?? throw new InvalidOperationException("The OpenTelemetry logger provider resource had no attributes.");

            events.Add(new CapturedEvent(
                name,
                severityText,
                severityNumber,
                attributes,
                data.Body,
                data.Timestamp != default,
                !string.IsNullOrWhiteSpace(otelSeverityText),
                otelSeverityNumber.HasValue,
                otelSeverityText,
                otelSeverityNumber,
                resourceAttributes.Select(attribute =>
                    new KeyValuePair<string, object?>(attribute.Key, attribute.Value)).ToArray(),
                traceId.All(character => character == '0') ? null : traceId,
                spanId.All(character => character == '0') ? null : spanId));
        }

        private static object? ReadInternalLogRecordProperty(LogRecord record, string name)
        {
            var property = typeof(LogRecord).GetProperty(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"The OpenTelemetry LogRecord did not expose its {name} field.");
            return property.GetValue(record);
        }
    }

    [Fact]
    public async Task SourceEventsUseExactSeverityTextAndNumbers()
    {
        var events = new List<CapturedEvent>();
        var source = new ControlledSource();
        var admission = new ControlledAdmission();
        var statePath = Path.Combine(Path.GetTempPath(), $"source-telemetry-{Guid.NewGuid():N}.json");
        await using var factory = CreateFactory(statePath, events, source, admission, auditFailure: true);
        var cases = factory.Services.GetRequiredService<ICaseStateStore>();
        cases.CreateCase(CaseId, "owner");
        cases.CreateCase(OtherCaseId, "other-owner");
        var sessions = factory.Services.GetRequiredService<DemoSessionStore>();
        var reviewer = sessions.Create(new DemoUser("reviewer", "Reviewer", "technical-reviewer", [CaseId]));
        var outOfRole = sessions.Create(new DemoUser("legal", "Legal", "legal-reviewer", [CaseId]));
        var presenter = sessions.Create(new DemoUser("presenter", "Presenter", "presenter", [CaseId]));
        using var client = factory.CreateClient();
        var auditStore = Assert.IsType<AuditFailingCaseStore>(cases);
        using (var response = await Get(client, reviewer, $"/api/v1/cases/{OtherCaseId}/sources/technical/records"))
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using (var response = await Get(client, outOfRole, $"/api/v1/cases/{CaseId}/sources/technical/records"))
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        source.Unavailable = true;
        using (var response = await Get(client, reviewer, $"/api/v1/cases/{CaseId}/sources/technical/records"))
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        source.Unavailable = false;
        admission.Reject = true;
        using (var response = await Get(client, reviewer, $"/api/v1/cases/{CaseId}/sources/technical/records"))
            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        admission.Reject = false;
        source.RecordCount = 101;
        using (var response = await Get(client, reviewer, $"/api/v1/cases/{CaseId}/sources/technical/records"))
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        source.RecordCount = 1;
        source.WaitUntilCancelled = true;
        using (var response = await Get(client, reviewer, $"/api/v1/cases/{CaseId}/sources/technical/records"))
            Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        source.WaitUntilCancelled = false;
        auditStore.FailAudit = false;
        using (var response = await PostMode(client, presenter, CaseId, "accepted-mode-change", 0, "unavailable"))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        auditStore.FailAudit = true;
        using (var response = await PostMode(client, presenter, CaseId, "failed-mode-change", 1, "available"))
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var logger = factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger("source-fixture-test");
        var environmentName = factory.Services.GetRequiredService<IHostEnvironment>().EnvironmentName;
        Assert.Throws<SourceFixtureRefusedException>(() => SourceFixtureLoader.LoadPinned(
            Path.Combine(Path.GetTempPath(), $"missing-source-fixtures-{Guid.NewGuid():N}"),
            logger,
            environmentName));
        Assert.Throws<IOException>(() => SourceFixtureLoader.LoadPinnedAndPublish(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "sources"),
            new SourceFixtureStore(PublishedFixtureState.Empty),
            _ => throw new IOException("simulated fixture publication failure"),
            logger,
            environmentName));
        var expected = new Dictionary<string, (string Severity, int Number)>(StringComparer.Ordinal)
        {
            ["source.mode_change.accepted"] = ("INFO", 9),
            ["source.read.denied_cross_case"] = ("WARN", 13),
            ["source.read.denied_out_of_role"] = ("WARN", 13),
            ["source.fixture.rejected"] = ("ERROR", 17),
            ["source.read.unavailable"] = ("WARN", 13),
            ["source.read.rate_or_concurrency_limited"] = ("WARN", 13),
            ["source.read.record_limit_rejected"] = ("WARN", 13),
            ["source.read.timed_out"] = ("WARN", 13),
            ["source.mode_change.audit_write_failed"] = ("ERROR", 17),
        };
        foreach (var (name, severity) in expected)
        {
            var matches = events.Where(item => item.Name == name).ToArray();
            var expectedCount = name == "source.fixture.rejected" ? 2 : 1;
            Assert.True(matches.Length == expectedCount, $"Expected {expectedCount} '{name}' event(s), captured: {string.Join(" | ", events.Select(item => $"{item.Name} [{string.Join(", ", item.Attributes.Select(attribute => $"{attribute.Key}={attribute.Value}"))}]"))}");
            foreach (var record in matches)
            {
                Assert.Equal(severity.Severity, record.SeverityText);
                Assert.Equal(severity.Number, record.SeverityNumber);
                var contractAttributes = record.Attributes
                    .Where(attribute => attribute.Key is "deployment.environment" or "event.name" or "severity.number"
                        or "severity.text" or "service.name" or "service.version")
                    .ToDictionary(attribute => attribute.Key, attribute => attribute.Value, StringComparer.Ordinal);
                Assert.Equal(
                    new[] { "deployment.environment", "event.name", "severity.number", "severity.text", "service.name", "service.version" }
                        .OrderBy(key => key, StringComparer.Ordinal),
                    contractAttributes.Keys.OrderBy(key => key, StringComparer.Ordinal));
                Assert.Equal(name, contractAttributes["event.name"]);
                Assert.Equal(severity.Severity, contractAttributes["severity.text"]);
                Assert.Equal(severity.Number, Convert.ToInt32(contractAttributes["severity.number"]));
                Assert.Equal(TelemetryRedaction.ServiceName, contractAttributes["service.name"]);
                Assert.Equal(TelemetryRedaction.ServiceVersion, contractAttributes["service.version"]);
                Assert.Equal(environmentName.ToLowerInvariant(), contractAttributes["deployment.environment"]);
            }
        }
        var output = string.Join("\n", events.SelectMany(item =>
            item.Attributes.Select(attribute => $"{attribute.Key}={attribute.Value}").Append(item.Body ?? string.Empty)));
        Assert.DoesNotContain(PayloadCanary, output);
        Assert.DoesNotContain(reviewer.Id, output);
        Assert.DoesNotContain(reviewer.CsrfToken, output);
        Assert.DoesNotContain(presenter.Id, output);
        Assert.DoesNotContain(presenter.CsrfToken, output);
        Assert.DoesNotContain("source.id", output);
        Assert.DoesNotContain("case.id", output);
        Assert.DoesNotContain("event.outcome", output);
        Assert.DoesNotContain("duration", output);
    }

    [Fact]
    public async Task EventsContainRequiredOtelAndTraceFields()
    {
        using var temporaryDirectory = new TemporaryDirectory("source-telemetry-fields-");
        var events = new List<CapturedEvent>();
        await using var factory = CreateFactory(
            Path.Combine(temporaryDirectory.Path, "case-state.json"),
            events,
            new ControlledSource(),
            new ControlledAdmission(),
            auditFailure: false);
        var logger = factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger("source-fields-test");
        var environmentName = factory.Services.GetRequiredService<IHostEnvironment>().EnvironmentName;
        var previousActivity = Activity.Current;

        try
        {
            Activity.Current = null;
            SourceSystemDiagnostics.SourceUnavailable(logger, environmentName);
            using (new Activity("source-active-span").Start())
            {
                SourceSystemDiagnostics.ReadLimited(logger, environmentName);
            }
        }
        finally
        {
            Activity.Current = previousActivity;
        }

        var outsideSpan = Assert.Single(events, item => item.Name == TelemetryRedaction.SourceEvents.SourceUnavailable);
        var insideSpan = Assert.Single(events, item => item.Name == TelemetryRedaction.SourceEvents.ReadLimited);
        var expectedResourceKeys = new[]
        {
            "deployment.environment",
            "service.name",
            "service.version",
            "telemetry.sdk.language",
            "telemetry.sdk.name",
            "telemetry.sdk.version",
        };

        foreach (var captured in new[] { outsideSpan, insideSpan })
        {
            Assert.True(captured.HasTimestamp);
            Assert.True(captured.HasOtelSeverityText);
            Assert.True(captured.HasOtelSeverityNumber);
            Assert.Equal("Warning", captured.OtelSeverityText);
            Assert.Equal(13, captured.OtelSeverityNumber);
            Assert.Equal("diagnostic event", captured.Body);
            Assert.NotEmpty(captured.Attributes);
            Assert.Equal(
                expectedResourceKeys,
                captured.Resource.Select(attribute => attribute.Key).OrderBy(key => key, StringComparer.Ordinal));
            Assert.All(captured.Resource, attribute => Assert.NotNull(attribute.Value));
        }

        Assert.Null(outsideSpan.TraceId);
        Assert.Null(outsideSpan.SpanId);
        Assert.False(string.IsNullOrWhiteSpace(insideSpan.TraceId));
        Assert.False(string.IsNullOrWhiteSpace(insideSpan.SpanId));
    }

    [Fact]
    public async Task SourceAttributesRequireApprovalAndNeverBecomeMetricDimensions()
    {
        using var temporaryDirectory = new TemporaryDirectory("source-telemetry-approval-");
        var approvalPath = Path.Combine(
            temporaryDirectory.Path,
            "docs",
            "telemetry",
            "source-system-proposed-additions.md");
        Directory.CreateDirectory(Path.GetDirectoryName(approvalPath)!);
        await File.WriteAllLinesAsync(approvalPath,
        [
            "source-specific-attributes",
            "source.id",
            "case.id",
            "event.outcome",
            "duration",
            "metric-dimensions",
        ]);

        var approvalLines = await File.ReadAllLinesAsync(approvalPath);
        var approvedSection = Array.IndexOf(approvalLines, "source-specific-attributes");
        var metricSection = Array.IndexOf(approvalLines, "metric-dimensions");
        Assert.True(approvedSection >= 0);
        Assert.True(metricSection > approvedSection);
        var permittedAttributes = approvalLines[(approvedSection + 1)..metricSection];
        var metricDimensions = approvalLines[(metricSection + 1)..];
        var expectedPermittedAttributes = new[] { "source.id", "case.id", "event.outcome", "duration" };
        var expectedMetricDimensions = Array.Empty<string>();
        Assert.Equal(
            expectedPermittedAttributes.OrderBy(attribute => attribute, StringComparer.Ordinal),
            permittedAttributes.OrderBy(attribute => attribute, StringComparer.Ordinal));
        Assert.Equal(expectedMetricDimensions, metricDimensions);
        Assert.Empty(permittedAttributes.Intersect(metricDimensions, StringComparer.Ordinal));

        var productionApprovalPath = Path.Combine(
            FindRepositoryRoot(),
            "docs",
            "telemetry",
            "source-system-proposed-additions.md");
        Assert.False(File.Exists(productionApprovalPath));

        var defaultFilteredAttributes = TelemetryRedaction.Redact(permittedAttributes.Select(attribute =>
            new KeyValuePair<string, object?>(attribute, "approval-fixture-value")));
        Assert.Empty(defaultFilteredAttributes);

        var events = new List<CapturedEvent>();
        await using var factory = CreateFactory(
            Path.Combine(temporaryDirectory.Path, "case-state.json"),
            events,
            new ControlledSource(),
            new ControlledAdmission(),
            auditFailure: false);
        var logger = factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger("source-approval-test");
        SourceSystemDiagnostics.SourceUnavailable(
            logger,
            factory.Services.GetRequiredService<IHostEnvironment>().EnvironmentName);

        var sourceEvent = Assert.Single(events, item => item.Name == TelemetryRedaction.SourceEvents.SourceUnavailable);
        var emittedAttributeNames = sourceEvent.Attributes.Select(attribute => attribute.Key).ToHashSet(StringComparer.Ordinal);
        Assert.Empty(permittedAttributes.Intersect(emittedAttributeNames, StringComparer.Ordinal));
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AirlineDemo.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository root from the test output directory.");
    }

    private static WebApplicationFactory<Program> CreateFactory(
        string statePath, List<CapturedEvent> events, ControlledSource source, ControlledAdmission admission, bool auditFailure) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CaseState:Path"] = statePath,
                ["Demo:Passcode"] = "telemetry-test-passcode",
            }));
            builder.ConfigureLogging(logging => logging.AddOpenTelemetry(options =>
            {
                var resourceBuilder = ResourceBuilder.CreateEmpty().AddAttributes(TestResourceAttributes());
                options.SetResourceBuilder(resourceBuilder);
                options.IncludeScopes = true;
                options.AddRedaction();
                options.AddProcessor(new EventCapture(events));
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new FixedTime());
                services.RemoveAll<ISourceSystem<TechnicalPayload>>();
                services.AddSingleton<ISourceSystem<TechnicalPayload>>(source);
                services.RemoveAll<ISourceReadAdmissionStore>();
                services.AddSingleton<ISourceReadAdmissionStore>(admission);
                if (auditFailure)
                {
                    services.RemoveAll<ICaseStateStore>();
                    services.AddSingleton<ICaseStateStore>(_ => new AuditFailingCaseStore(new CaseStateStore(statePath)));
                }
            });
        });

    private static IReadOnlyList<KeyValuePair<string, object>> TestResourceAttributes()
    {
        var attributes = TelemetryRedaction.ResourceAttributes(
                TelemetryRedaction.ServiceVersion,
                "test")
            .Select(attribute => new KeyValuePair<string, object>(attribute.Key, attribute.Value))
            .ToList();
        attributes.AddRange(
        [
            new("telemetry.sdk.language", "dotnet"),
            new("telemetry.sdk.name", "opentelemetry"),
            new("telemetry.sdk.version", typeof(LogRecord).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"),
        ]);
        return attributes;
    }

    private static Task<HttpResponseMessage> Get(HttpClient client, DemoSession session, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", $"__Host-demo-session={session.Id}");
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> PostMode(
        HttpClient client, DemoSession session, string caseId, string commandId, long revision, string mode)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/cases/{caseId}/sources/technical/mode")
        {
            Content = JsonContent.Create(new { commandId, expectedRevision = revision, mode }),
        };
        request.Headers.Add("Cookie", $"__Host-demo-session={session.Id}");
        request.Headers.Add("X-CSRF-Token", session.CsrfToken);
        return client.SendAsync(request);
    }
}

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
        bool HasAttributes,
        bool HasOtelSeverityText,
        bool HasOtelSeverityNumber,
        string? OtelSeverityText,
        int? OtelSeverityNumber,
        IReadOnlyList<KeyValuePair<string, object?>> Resource,
        string? TraceId,
        string? SpanId);

    private sealed class EventCapture(List<CapturedEvent> events, string? countedEventName = null) : BaseProcessor<LogRecord>
    {
        private int capturedCount;

        public int CapturedCount => Volatile.Read(ref capturedCount);

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
                data.Attributes is not null,
                !string.IsNullOrWhiteSpace(otelSeverityText),
                otelSeverityNumber.HasValue,
                otelSeverityText,
                otelSeverityNumber,
                resourceAttributes.Select(attribute =>
                    new KeyValuePair<string, object?>(attribute.Key, attribute.Value)).ToArray(),
                traceId.All(character => character == '0') ? null : traceId,
                spanId.All(character => character == '0') ? null : spanId));
            if (countedEventName is null || string.Equals(name, countedEventName, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref capturedCount);
            }
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
    public void EventsContainRequiredOtelAndTraceFields()
    {
        var events = new List<CapturedEvent>();
        var capture = new EventCapture(events);
        using var services = CreateProductionLoggerServices(capture);
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("source-fields-test");
        var environmentName = Environments.Development;
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
            "service.name",
            "telemetry.sdk.language",
            "telemetry.sdk.name",
            "telemetry.sdk.version",
        };

        foreach (var (captured, expectedName) in new[]
        {
            (outsideSpan, TelemetryRedaction.SourceEvents.SourceUnavailable),
            (insideSpan, TelemetryRedaction.SourceEvents.ReadLimited),
        })
        {
            Assert.True(captured.HasTimestamp);
            Assert.True(captured.HasAttributes);
            Assert.True(captured.HasOtelSeverityText);
            Assert.True(captured.HasOtelSeverityNumber);
            Assert.Equal("Warning", captured.OtelSeverityText);
            Assert.Equal(13, captured.OtelSeverityNumber);
            Assert.Equal("diagnostic event", captured.Body);
            var attributes = captured.Attributes.ToDictionary(attribute => attribute.Key, attribute => attribute.Value, StringComparer.Ordinal);
            Assert.Equal(
                new[] { "deployment.environment", "event.name", "severity.number", "severity.text", "service.name", "service.version" }
                    .OrderBy(key => key, StringComparer.Ordinal),
                attributes.Keys.OrderBy(key => key, StringComparer.Ordinal));
            Assert.Equal(expectedName, attributes["event.name"]);
            Assert.Equal(TelemetryRedaction.ServiceName, attributes["service.name"]);
            Assert.Equal(TelemetryRedaction.ServiceVersion, attributes["service.version"]);
            Assert.Equal(environmentName.ToLowerInvariant(), attributes["deployment.environment"]);
            Assert.Equal("WARN", attributes["severity.text"]);
            Assert.Equal(13, Convert.ToInt32(attributes["severity.number"]));
            Assert.Equal(
                expectedResourceKeys,
                captured.Resource.Select(attribute => attribute.Key).OrderBy(key => key, StringComparer.Ordinal));
            Assert.All(captured.Resource, attribute => Assert.NotNull(attribute.Value));
            var resource = captured.Resource.ToDictionary(attribute => attribute.Key, attribute => attribute.Value, StringComparer.Ordinal);
            Assert.Equal("opentelemetry", resource["telemetry.sdk.name"]);
            Assert.Equal("dotnet", resource["telemetry.sdk.language"]);
            Assert.False(string.IsNullOrWhiteSpace(resource["telemetry.sdk.version"]?.ToString()));
        }

        Assert.Null(outsideSpan.TraceId);
        Assert.Null(outsideSpan.SpanId);
        Assert.False(string.IsNullOrWhiteSpace(insideSpan.TraceId));
        Assert.False(string.IsNullOrWhiteSpace(insideSpan.SpanId));
    }

    [Fact]
    public void SourceEventsNeverCarryRecordIdentifiersOrPayloads()
    {
        using var temporaryDirectory = new TemporaryDirectory("source-telemetry-private-");
        var events = new List<CapturedEvent>();
        var capture = new EventCapture(events, TelemetryRedaction.SourceEvents.SourceUnavailable);
        var stateStore = new CaseStateStore(Path.Combine(temporaryDirectory.Path, "case-state.json"));
        stateStore.CreateCase(CaseId, "owner");
        using var services = CreateProductionLoggerServices(capture);
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("source-private-test");
        var environmentName = Environments.Development;
        const string RawRecordId = "SYN-SOURCE-RECORD-RAW-01";
        const string RecordId = "SYN-SOURCE-RECORD-ID-01";
        const string DottedRecordId = "SYN-SOURCE-RECORD-DOT-01";
        const string Payload = "SYN-SOURCE-PAYLOAD-01";

        using (logger.BeginScope(new Dictionary<string, object?>
        {
            ["source.raw_record_id"] = RawRecordId,
            ["recordId"] = RecordId,
            ["record.id"] = DottedRecordId,
            ["payload"] = Payload,
        }))
        {
            SourceSystemDiagnostics.SourceUnavailable(logger, environmentName);
        }

        var emitted = Assert.Single(events, item => item.Name == TelemetryRedaction.SourceEvents.SourceUnavailable);
        Assert.Equal(1, capture.CapturedCount);
        var attributeNames = emitted.Attributes.Select(attribute => attribute.Key).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("source.raw_record_id", attributeNames);
        Assert.DoesNotContain("recordId", attributeNames);
        Assert.DoesNotContain("record.id", attributeNames);
        var output = string.Join("\n",
            emitted.Attributes.Select(attribute => $"{attribute.Key}={attribute.Value}").Append(emitted.Body ?? string.Empty));
        Assert.DoesNotContain(RawRecordId, output);
        Assert.DoesNotContain(RecordId, output);
        Assert.DoesNotContain(DottedRecordId, output);
        Assert.DoesNotContain(Payload, output);
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

        var sourceAttributeValues = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["source.id"] = "technical",
            ["case.id"] = CaseId,
            ["event.outcome"] = "unavailable",
            ["duration"] = 42,
            ["source.raw_record_id"] = "record-1",
        };
        var approvedAttributeSet = permittedAttributes.ToHashSet(StringComparer.Ordinal);
        var approvedAttributeValues = sourceAttributeValues
            .Where(attribute => approvedAttributeSet.Contains(attribute.Key))
            .ToDictionary(attribute => attribute.Key, attribute => attribute.Value, StringComparer.Ordinal);
        Assert.Equal(
            expectedPermittedAttributes.OrderBy(attribute => attribute, StringComparer.Ordinal),
            approvedAttributeValues.Keys.OrderBy(attribute => attribute, StringComparer.Ordinal));
        Assert.DoesNotContain("source.raw_record_id", approvedAttributeValues.Keys);
        var approvedEvents = new List<CapturedEvent>();
        await using var approvedFactory = CreateFactory(
            Path.Combine(temporaryDirectory.Path, "case-state.json"),
            approvedEvents,
            new ControlledSource(),
            new ControlledAdmission(),
            auditFailure: false);
        var approvedLogger = approvedFactory.Services.GetRequiredService<ILoggerFactory>().CreateLogger("source-approval-test");
        using (approvedLogger.BeginScope(approvedAttributeValues))
        {
            SourceSystemDiagnostics.SourceUnavailable(
                approvedLogger,
                approvedFactory.Services.GetRequiredService<IHostEnvironment>().EnvironmentName);
        }

        var approvedSourceEvent = Assert.Single(approvedEvents, item => item.Name == TelemetryRedaction.SourceEvents.SourceUnavailable);
        var emittedApprovedAttributes = approvedSourceEvent.Attributes
            .Where(attribute => expectedPermittedAttributes.Contains(attribute.Key, StringComparer.Ordinal))
            .ToDictionary(attribute => attribute.Key, attribute => attribute.Value, StringComparer.Ordinal);
        Assert.Equal(
            expectedPermittedAttributes.OrderBy(attribute => attribute, StringComparer.Ordinal),
            emittedApprovedAttributes.Keys.OrderBy(attribute => attribute, StringComparer.Ordinal));
        foreach (var (key, value) in approvedAttributeValues)
        {
            Assert.Equal(value, emittedApprovedAttributes[key]);
        }

        var defaultEvents = new List<CapturedEvent>();
        await using var defaultFactory = CreateFactory(
            Path.Combine(temporaryDirectory.Path, "default-case-state.json"),
            defaultEvents,
            new ControlledSource(),
            new ControlledAdmission(),
            auditFailure: false);
        var defaultLogger = defaultFactory.Services.GetRequiredService<ILoggerFactory>().CreateLogger("source-default-approval-test");
        var productionApprovalSet = File.Exists(productionApprovalPath)
            ? (await File.ReadAllLinesAsync(productionApprovalPath))
                .SkipWhile(line => line != "source-specific-attributes")
                .Skip(1)
                .TakeWhile(line => line != "metric-dimensions")
                .ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var defaultAttributeValues = sourceAttributeValues
            .Where(attribute => productionApprovalSet.Contains(attribute.Key))
            .ToDictionary(attribute => attribute.Key, attribute => attribute.Value, StringComparer.Ordinal);
        Assert.Empty(defaultAttributeValues);
        using (defaultLogger.BeginScope(defaultAttributeValues))
        {
            SourceSystemDiagnostics.SourceUnavailable(
                defaultLogger,
                defaultFactory.Services.GetRequiredService<IHostEnvironment>().EnvironmentName);
        }

        var defaultSourceEvent = Assert.Single(defaultEvents, item => item.Name == TelemetryRedaction.SourceEvents.SourceUnavailable);
        var defaultEmittedAttributeNames = defaultSourceEvent.Attributes.Select(attribute => attribute.Key).ToHashSet(StringComparer.Ordinal);
        Assert.Empty(expectedPermittedAttributes.Intersect(defaultEmittedAttributeNames, StringComparer.Ordinal));
        Assert.DoesNotContain("source.raw_record_id", defaultEmittedAttributeNames);
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

    private static ServiceProvider CreateProductionLoggerServices(EventCapture processor)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [DiagnosticsSetup.ConnectionStringSetting] =
                "InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=http://127.0.0.1:1/",
        }).Build();
        var services = new ServiceCollection();
        services.AddAirlineDiagnostics(configuration);
        services.AddLogging(logging => logging.AddOpenTelemetry(options => options.AddProcessor(processor)));
        return services.BuildServiceProvider();
    }

    private static WebApplicationFactory<Program> CreateFactory(
        string statePath,
        List<CapturedEvent> events,
        ControlledSource source,
        ControlledAdmission admission,
        bool auditFailure)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CaseState:Path"] = statePath,
                ["Demo:Passcode"] = "telemetry-test-passcode",
            }));
            builder.ConfigureLogging(logging => logging.AddOpenTelemetry(options =>
            {
                var resourceAttributes = TelemetryRedaction.ResourceAttributes(
                        TelemetryRedaction.ServiceVersion,
                        (builder.GetSetting(WebHostDefaults.EnvironmentKey) ?? Environments.Development).ToLowerInvariant())
                    .Select(attribute => new KeyValuePair<string, object>(attribute.Key, attribute.Value));
                var resourceBuilder = ResourceBuilder.CreateEmpty()
                    .AddTelemetrySdk()
                    .AddAttributes(resourceAttributes);
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

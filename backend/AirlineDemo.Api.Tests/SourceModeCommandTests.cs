using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Investigation;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Requests;
using AirlineDemo.Api.Runs;
using AirlineDemo.Api.SourceSystems;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AirlineDemo.Api.Tests;

public sealed class SourceModeCommandTests
{
    private const string Passcode = "test-passcode";
    private const string Case = "case-001";
    private const string Path = "/api/v1/cases/{0}/sources/{1}/mode";
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RevisionChangingCaseStateStore(ICaseStateStore inner) : ICaseStateStore
    {
        public bool ChangeRevisionOnNextCommit { get; set; }

        public CaseScope AuthorizeCase(string caseId, string owner) => inner.AuthorizeCase(caseId, owner);
        public string? OwnerOf(string caseId) => inner.OwnerOf(caseId);
        public CaseScope CreateCase(string caseId, string owner) => inner.CreateCase(caseId, owner);
        public CaseState Read(CaseScope scope) => inner.Read(scope);
        public BasisVersion GetBasisVersion(CaseScope scope, int version) => inner.GetBasisVersion(scope, version);
        public IReadOnlyList<AuditEvent> ReadAudit(CaseScope scope) => inner.ReadAudit(scope);
        public IReadOnlyList<CaseAssessment> ListAssessments(CaseScope scope) => inner.ListAssessments(scope);
        public CaseAssessment? FindAssessment(CaseScope scope, string assessmentId) => inner.FindAssessment(scope, assessmentId);
        public IReadOnlyList<DeliveryReceipt> ListRequestReceipts(CaseScope scope) => inner.ListRequestReceipts(scope);

        public CommitResult Commit(CaseScope scope, long expectedRevision, CaseCommit commit)
        {
            if (ChangeRevisionOnNextCommit && commit.Action == SourceModes.Action)
            {
                ChangeRevisionOnNextCommit = false;
                inner.Commit(scope, expectedRevision, new CaseCommit(null, $"race-{Guid.NewGuid():N}",
                    "concurrent-actor", "concurrent_case_update", "{}"));
            }

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

    private static readonly DemoUser Presenter = new("presenter", "Presenter", "presenter", ["case-001", "case-002"]);
    private static readonly DemoUser OtherPresenter = new("presenter-2", "Second presenter", "presenter", ["case-001"]);

    private static WebApplicationFactory<Program> CreateFactory(string statePath, bool changeRevisionOnNextCommit = false) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Demo:Passcode"] = Passcode,
                    ["CaseState:Path"] = statePath,
                }));
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<TimeProvider>(new FixedTime(Now));
                if (changeRevisionOnNextCommit)
                {
                    services.RemoveAll<ICaseStateStore>();
                    services.AddSingleton<ICaseStateStore>(new RevisionChangingCaseStateStore(new CaseStateStore(statePath)));
                }
            });
        });

    private static string TempStatePath() => System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"source-mode-{Guid.NewGuid():N}.json");

    private static ICaseStateStore Cases(WebApplicationFactory<Program> factory) => factory.Services.GetRequiredService<ICaseStateStore>();

    private static SourceModeStore Modes(WebApplicationFactory<Program> factory) => factory.Services.GetRequiredService<SourceModeStore>();

    private static CaseScope Ensure(ICaseStateStore cases)
    {
        if (cases.OwnerOf(Case) is null) cases.CreateCase(Case, Presenter.Id);
        return cases.AuthorizeCase(Case, Presenter.Id);
    }

    private static DemoSession Session(WebApplicationFactory<Program> factory, DemoUser user) =>
        factory.Services.GetRequiredService<DemoSessionStore>().Create(user);

    private static Task<HttpResponseMessage> PostModeAsync(HttpClient client, DemoSession? session, string source, object? body, string? csrf,
        string caseId = Case)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, string.Format(Path, caseId, source));
        if (body is not null) request.Content = JsonContent.Create(body);
        if (session is not null) request.Headers.Add("Cookie", $"__Host-demo-session={session.Id}");
        if (csrf is not null) request.Headers.Add("X-CSRF-Token", csrf);
        return client.SendAsync(request);
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code, string message)
    {
        var actual = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"Expected {status}, got {response.StatusCode}: {actual}");
        var expected = JsonSerializer.Serialize(new { contractVersion = 1, error = new { code, message } });
        Assert.Equal(expected, actual);
    }

    private static string ReceiptBody(string commandId, string caseId, string sourceId, string priorMode, string newMode,
        long priorRevision, long newRevision, string auditEventId) =>
        JsonSerializer.Serialize(new
        {
            contractVersion = 1,
            commandId,
            caseId,
            sourceId,
            priorMode,
            newMode,
            priorRevision,
            newRevision,
            auditEventId,
        });

    private static string ModeText(SourceModeStore modes, CaseState state) =>
        string.Join(",", modes.ModesFor(state).OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));

    private static string[] Names(JsonElement element) => element.EnumerateObject().Select(p => p.Name).ToArray();

    [Fact]
    public async Task AC014_valid_command_changes_one_mode_and_appends_exact_server_attributed_audit_event()
    {
        var path = TempStatePath();
        try
        {
            await using var factory = CreateFactory(path);
            var cases = Cases(factory);
            var scope = Ensure(cases);
            var modes = Modes(factory);
            var before = cases.Read(scope);
            var session = Session(factory, Presenter);

            using var client = factory.CreateClient();
            using var response = await PostModeAsync(client, session, "technical",
                new { commandId = "cmd-1", expectedRevision = before.Revision, mode = "unavailable", actor = "mallory", at = "2000-01-01T00:00:00Z", reason = "forged" },
                session.CsrfToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var receipt = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(new[] { "contractVersion", "commandId", "caseId", "sourceId", "priorMode", "newMode", "priorRevision", "newRevision", "auditEventId" }, Names(receipt));

            var after = cases.Read(scope);
            var newEvent = after.Audit[^1];
            Assert.Equal(1, receipt.GetProperty("contractVersion").GetInt32());
            Assert.Equal("cmd-1", receipt.GetProperty("commandId").GetString());
            Assert.Equal(Case, receipt.GetProperty("caseId").GetString());
            Assert.Equal("technical", receipt.GetProperty("sourceId").GetString());
            Assert.Equal("available", receipt.GetProperty("priorMode").GetString());
            Assert.Equal("unavailable", receipt.GetProperty("newMode").GetString());
            Assert.Equal((long)before.Revision, receipt.GetProperty("priorRevision").GetInt64());
            Assert.Equal((long)before.Revision + 1, receipt.GetProperty("newRevision").GetInt64());
            Assert.Equal($"audit-{newEvent.Sequence}", receipt.GetProperty("auditEventId").GetString());

            Assert.Equal(before.Audit.Count + 1, after.Audit.Count);
            Assert.Equal(before.Revision + 1, after.Revision);
            Assert.Equal(Presenter.Id, newEvent.Actor);
            Assert.Equal("presenter_set_source_mode", newEvent.Action);
            Assert.Equal(Now, newEvent.At);

            using var detail = JsonDocument.Parse(newEvent.Detail);
            Assert.Equal(new[] { "commandId", "caseId", "sourceId", "priorMode", "newMode", "priorRevision", "newRevision" }, Names(detail.RootElement));
            Assert.Equal("cmd-1", detail.RootElement.GetProperty("commandId").GetString());
            Assert.Equal(Case, detail.RootElement.GetProperty("caseId").GetString());
            Assert.Equal("technical", detail.RootElement.GetProperty("sourceId").GetString());
            Assert.Equal("available", detail.RootElement.GetProperty("priorMode").GetString());
            Assert.Equal("unavailable", detail.RootElement.GetProperty("newMode").GetString());
            Assert.Equal((long)before.Revision, detail.RootElement.GetProperty("priorRevision").GetInt64());
            Assert.Equal((long)before.Revision + 1, detail.RootElement.GetProperty("newRevision").GetInt64());
            Assert.DoesNotContain("mallory", newEvent.Detail);
            Assert.DoesNotContain(session.Id, newEvent.Detail);

            var afterModes = modes.ModesFor(after);
            Assert.Equal("unavailable", afterModes["technical"]);
            Assert.Equal(new[] { "available", "available", "available", "available" },
                afterModes.Where(kv => kv.Key != "technical").Select(kv => kv.Value).ToArray());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AC015_auth_role_scope_and_antiforgery_denials_leave_state_unchanged()
    {
        var path = TempStatePath();
        try
        {
            await using var factory = CreateFactory(path);
            var cases = Cases(factory);
            var scope = Ensure(cases);
            var modes = Modes(factory);
            var before = cases.Read(scope);
            var beforeModes = ModeText(modes, before);
            var presenter = Session(factory, Presenter);
            var body = new { commandId = "denied", expectedRevision = before.Revision, mode = "unavailable" };

            using var client = factory.CreateClient();
            using (var unauthenticated = await PostModeAsync(client, null, "technical", body, null))
            {
                await AssertErrorAsync(unauthenticated, HttpStatusCode.Unauthorized, "unauthenticated", "Not signed in.");
            }

            var roles = new[]
            {
                Session(factory, new DemoUser("reviewer", "Reviewer", "technical-reviewer", ["case-001"])),
                Session(factory, new DemoUser("fin-1", "Finance", "finance-reviewer", ["case-001"])),
                Session(factory, new DemoUser("legal-1", "Legal", "legal-reviewer", ["case-001"])),
                Session(factory, new DemoUser("exec-1", "Executive", "executive", ["case-001"])),
            };
            foreach (var role in roles)
            {
                using var response = await PostModeAsync(client, role, "technical", body, role.CsrfToken);
                await AssertErrorAsync(response, HttpStatusCode.Forbidden, "forbidden", "Only a presenter can change source modes.");
            }

            var outsider = Session(factory, new DemoUser("presenter-3", "Other case", "presenter", ["case-002"]));
            using (var outOfScope = await PostModeAsync(client, outsider, "technical", body, outsider.CsrfToken))
            {
                await AssertErrorAsync(outOfScope, HttpStatusCode.NotFound, "not_found", "Case not found.");
            }

            using (var missingCsrf = await PostModeAsync(client, presenter, "technical", body, null))
            {
                await AssertErrorAsync(missingCsrf, HttpStatusCode.Forbidden, "anti_forgery_failed", "Anti-forgery validation failed.");
            }

            using (var wrongCsrf = await PostModeAsync(client, presenter, "technical", body, "not-the-token"))
            {
                await AssertErrorAsync(wrongCsrf, HttpStatusCode.Forbidden, "anti_forgery_failed", "Anti-forgery validation failed.");
            }

            var after = cases.Read(scope);
            Assert.Equal(before.Revision, after.Revision);
            Assert.Equal(before.Audit.Count, after.Audit.Count);
            Assert.Equal(beforeModes, ModeText(modes, after));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AC016_replay_conflict_stale_unchanged_and_reset_have_exact_results()
    {
        var path = TempStatePath();
        try
        {
            await using var factory = CreateFactory(path);
            var cases = Cases(factory);
            var scope = Ensure(cases);
            var modes = Modes(factory);
            var presenter = Session(factory, Presenter);
            var second = Session(factory, OtherPresenter);
            using var client = factory.CreateClient();

            var start = cases.Read(scope);
            var payload = new { commandId = "cmd-replay", expectedRevision = start.Revision, mode = "stale" };
            using var first = await PostModeAsync(client, presenter, "finance", payload, presenter.CsrfToken);
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            var firstReceipt = await first.Content.ReadFromJsonAsync<JsonElement>();
            var afterFirst = cases.Read(scope);
            Assert.Equal(start.Revision + 1, afterFirst.Revision);
            Assert.Equal(start.Audit.Count + 1, afterFirst.Audit.Count);

            using (var replay = await PostModeAsync(client, presenter, "finance", payload, presenter.CsrfToken))
            {
                Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
                Assert.Equal(firstReceipt.ToString(), (await replay.Content.ReadFromJsonAsync<JsonElement>()).ToString());
            }

            var snapshotRevision = afterFirst.Revision;
            var snapshotAudit = afterFirst.Audit.Count;
            var snapshotModes = ModeText(modes, afterFirst);

            using (var otherActor = await PostModeAsync(client, second, "finance", payload, second.CsrfToken))
            {
                await AssertErrorAsync(otherActor, HttpStatusCode.Conflict, "command_id_conflict",
                    "This command ID was already used for a different command.");
            }

            using (var otherPayload = await PostModeAsync(client, presenter, "finance", new { commandId = "cmd-replay", expectedRevision = snapshotRevision, mode = "conflicting" }, presenter.CsrfToken))
            {
                await AssertErrorAsync(otherPayload, HttpStatusCode.Conflict, "command_id_conflict",
                    "This command ID was already used for a different command.");
            }

            using (var stale = await PostModeAsync(client, presenter, "records", new { commandId = "cmd-stale", expectedRevision = start.Revision, mode = "unavailable" }, presenter.CsrfToken))
            {
                await AssertErrorAsync(stale, HttpStatusCode.Conflict, "stale_revision",
                    "The case revision has changed; reload and retry.");
            }

            using (var unchanged = await PostModeAsync(client, presenter, "finance", new { commandId = "cmd-same", expectedRevision = snapshotRevision, mode = "stale" }, presenter.CsrfToken))
            {
                await AssertErrorAsync(unchanged, (HttpStatusCode)422, "mode_unchanged", "The source already has this mode.");
            }

            var afterRejections = cases.Read(scope);
            Assert.Equal(snapshotRevision, afterRejections.Revision);
            Assert.Equal(snapshotAudit, afterRejections.Audit.Count);
            Assert.Equal(snapshotModes, ModeText(modes, afterRejections));

            var reset = factory.Services.GetRequiredService<DemoRunReset>().Reset(Case, Presenter.Id, "case-001-reset", "seed-fixtures", "test-version");
            Assert.All(SourceModes.Sources, source => Assert.Equal("available", modes.ModesFor(reset.NewRun)[source]));
            var priorAfterReset = cases.Read(scope);
            Assert.Equal(snapshotRevision, priorAfterReset.Revision);
            Assert.Equal(snapshotAudit, priorAfterReset.Audit.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AC015_AC016_every_mode_response_uses_contract_v1_and_errors_have_no_receipt()
    {
        var path = TempStatePath();
        try
        {
            await using var factory = CreateFactory(path);
            var cases = Cases(factory);
            var scope = Ensure(cases);
            var modes = Modes(factory);
            var before = cases.Read(scope);
            var presenter = Session(factory, Presenter);
            using var client = factory.CreateClient();
            var body = new { commandId = "cmd-envelope", expectedRevision = before.Revision, mode = "stale" };

            using (var response = await PostModeAsync(client, null, "technical", body, null))
                await AssertErrorAsync(response, HttpStatusCode.Unauthorized, "unauthenticated", "Not signed in.");

            var reviewer = Session(factory, new DemoUser("reviewer-envelope", "Reviewer", "technical-reviewer", [Case]));
            using (var response = await PostModeAsync(client, reviewer, "technical", body, reviewer.CsrfToken))
                await AssertErrorAsync(response, HttpStatusCode.Forbidden, "forbidden", "Only a presenter can change source modes.");

            var outsider = Session(factory, new DemoUser("outsider-envelope", "Outsider", "presenter", ["case-002"]));
            using (var response = await PostModeAsync(client, outsider, "technical", body, null))
                await AssertErrorAsync(response, HttpStatusCode.NotFound, "not_found", "Case not found.");

            using (var response = await PostModeAsync(client, presenter, "technical", body, null))
                await AssertErrorAsync(response, HttpStatusCode.Forbidden, "anti_forgery_failed", "Anti-forgery validation failed.");
            using (var response = await PostModeAsync(client, presenter, "technical", body, "wrong-token"))
                await AssertErrorAsync(response, HttpStatusCode.Forbidden, "anti_forgery_failed", "Anti-forgery validation failed.");

            using (var response = await PostModeAsync(client, presenter, "technical", null, presenter.CsrfToken))
                await AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request",
                    "commandId, expectedRevision and mode are required.");
            using (var response = await PostModeAsync(client, presenter, "technical",
                new { expectedRevision = before.Revision, mode = "stale" }, presenter.CsrfToken))
                await AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request",
                    "commandId, expectedRevision and mode are required.");
            using (var response = await PostModeAsync(client, presenter, "technical",
                new { commandId = "missing-revision", expectedRevision = (long?)null, mode = "stale" }, presenter.CsrfToken))
                await AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request",
                    "commandId, expectedRevision and mode are required.");
            using (var response = await PostModeAsync(client, presenter, "technical",
                new { commandId = "missing-mode", expectedRevision = before.Revision }, presenter.CsrfToken))
                await AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request",
                    "commandId, expectedRevision and mode are required.");
            using (var response = await PostModeAsync(client, presenter, "technical",
                new { commandId = new string('c', 65), expectedRevision = before.Revision, mode = "stale" }, presenter.CsrfToken))
                await AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request",
                    "commandId must be at most 64 characters.");

            using (var response = await PostModeAsync(client, presenter, "not-a-source",
                new { commandId = "unknown-source", expectedRevision = before.Revision, mode = "stale" }, presenter.CsrfToken))
                await AssertErrorAsync(response, (HttpStatusCode)422, "unknown_source",
                    "Source is not one of the five case sources.");
            using (var response = await PostModeAsync(client, presenter, "technical",
                new { commandId = "invalid-mode", expectedRevision = before.Revision, mode = "unknown" }, presenter.CsrfToken))
                await AssertErrorAsync(response, (HttpStatusCode)422, "invalid_mode",
                    "Mode must be available, unavailable, stale or conflicting.");

            using var accepted = await PostModeAsync(client, presenter, "finance", body, presenter.CsrfToken);
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            var afterAccepted = cases.Read(scope);
            var receiptBody = ReceiptBody("cmd-envelope", Case, "finance", "available", "stale",
                before.Revision, before.Revision + 1, $"audit-{afterAccepted.Audit[^1].Sequence}");
            Assert.Equal(receiptBody, await accepted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            using (var replay = await PostModeAsync(client, presenter, "finance", body, presenter.CsrfToken))
            {
                Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
                Assert.Equal(receiptBody, await replay.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            }

            var secondPresenter = Session(factory, OtherPresenter);
            using (var response = await PostModeAsync(client, secondPresenter, "finance", body, secondPresenter.CsrfToken))
                await AssertErrorAsync(response, HttpStatusCode.Conflict, "command_id_conflict",
                    "This command ID was already used for a different command.");
            using (var response = await PostModeAsync(client, presenter, "finance",
                new { commandId = "cmd-envelope", expectedRevision = before.Revision, mode = "conflicting" }, presenter.CsrfToken))
                await AssertErrorAsync(response, HttpStatusCode.Conflict, "command_id_conflict",
                    "This command ID was already used for a different command.");
            using (var response = await PostModeAsync(client, presenter, "records",
                new { commandId = "stale-envelope", expectedRevision = before.Revision, mode = "unavailable" }, presenter.CsrfToken))
                await AssertErrorAsync(response, HttpStatusCode.Conflict, "stale_revision",
                    "The case revision has changed; reload and retry.");
            using (var response = await PostModeAsync(client, presenter, "finance",
                new { commandId = "unchanged-envelope", expectedRevision = afterAccepted.Revision, mode = "stale" }, presenter.CsrfToken))
                await AssertErrorAsync(response, (HttpStatusCode)422, "mode_unchanged", "The source already has this mode.");

            var afterRejections = cases.Read(scope);
            Assert.Equal(afterAccepted.Revision, afterRejections.Revision);
            Assert.Equal(afterAccepted.Audit.Count, afterRejections.Audit.Count);
            Assert.Equal(ModeText(modes, afterAccepted), ModeText(modes, afterRejections));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task M03_v1_error_envelopes_preserve_command_security_invariants()
    {
        var path = TempStatePath();
        try
        {
            await using var factory = CreateFactory(path, changeRevisionOnNextCommit: true);
            var cases = Cases(factory);
            var scope = Ensure(cases);
            var modeStore = Modes(factory);
            var raceStore = Assert.IsType<RevisionChangingCaseStateStore>(cases);
            var before = cases.Read(scope);
            var presenter = Session(factory, Presenter);
            var secondPresenter = Session(factory, OtherPresenter);
            using var client = factory.CreateClient();

            var outsider = Session(factory, new DemoUser("presenter-outside", "Outside scope", "presenter", ["case-002"]));
            using (var outOfScope = await PostModeAsync(client, outsider, "technical",
                new { commandId = "scope-first", expectedRevision = before.Revision, mode = "unavailable" }, null))
                await AssertErrorAsync(outOfScope, HttpStatusCode.NotFound, "not_found", "Case not found.");

            var acceptedBody = new
            {
                commandId = "m03-accepted",
                expectedRevision = before.Revision,
                mode = "unavailable",
                actor = "forged-actor",
                at = "2000-01-01T00:00:00Z",
                reason = "forged-reason",
            };
            using (var accepted = await PostModeAsync(client, presenter, "technical", acceptedBody, presenter.CsrfToken))
            {
                Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
                var afterAccepted = cases.Read(scope);
                var receipt = ReceiptBody("m03-accepted", Case, "technical", "available", "unavailable",
                    before.Revision, before.Revision + 1, $"audit-{afterAccepted.Audit[^1].Sequence}");
                Assert.Equal(receipt, await accepted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
                Assert.Equal(before.Audit.Count + 1, afterAccepted.Audit.Count);
                Assert.Equal(before.Audit.Count(a => a.Action == SourceModes.Action) + 1,
                    afterAccepted.Audit.Count(a => a.Action == SourceModes.Action));

                var acceptedEvent = Assert.Single(afterAccepted.Audit.Where(a => a.Action == SourceModes.Action));
                Assert.Equal(Presenter.Id, acceptedEvent.Actor);
                Assert.Equal(SourceModes.Action, acceptedEvent.Action);
                Assert.Equal(Now, acceptedEvent.At);
                Assert.Equal(JsonSerializer.Serialize(new
                {
                    commandId = "m03-accepted",
                    caseId = Case,
                    sourceId = "technical",
                    priorMode = "available",
                    newMode = "unavailable",
                    priorRevision = before.Revision,
                    newRevision = before.Revision + 1,
                }), acceptedEvent.Detail);
            }

            var acceptedState = cases.Read(scope);
            var acceptedModes = ModeText(modeStore, acceptedState);
            using (var replayWithoutCsrf = await PostModeAsync(client, presenter, "technical", acceptedBody, null))
                await AssertErrorAsync(replayWithoutCsrf, HttpStatusCode.Forbidden, "anti_forgery_failed",
                    "Anti-forgery validation failed.");
            using (var otherActor = await PostModeAsync(client, secondPresenter, "technical", acceptedBody, secondPresenter.CsrfToken))
                await AssertErrorAsync(otherActor, HttpStatusCode.Conflict, "command_id_conflict",
                    "This command ID was already used for a different command.");
            using (var otherPayload = await PostModeAsync(client, presenter, "technical",
                new { commandId = "m03-accepted", expectedRevision = before.Revision, mode = "stale" }, presenter.CsrfToken))
                await AssertErrorAsync(otherPayload, HttpStatusCode.Conflict, "command_id_conflict",
                    "This command ID was already used for a different command.");
            using (var otherSource = await PostModeAsync(client, presenter, "finance",
                new { commandId = "m03-accepted", expectedRevision = before.Revision, mode = "unavailable" }, presenter.CsrfToken))
                await AssertErrorAsync(otherSource, HttpStatusCode.Conflict, "command_id_conflict",
                    "This command ID was already used for a different command.");
            using (var otherRevision = await PostModeAsync(client, presenter, "technical",
                new { commandId = "m03-accepted", expectedRevision = before.Revision + 1, mode = "unavailable" }, presenter.CsrfToken))
                await AssertErrorAsync(otherRevision, HttpStatusCode.Conflict, "command_id_conflict",
                    "This command ID was already used for a different command.");
            using (var unknownSource = await PostModeAsync(client, presenter, "unknown",
                new { commandId = "m03-accepted", expectedRevision = before.Revision, mode = "unavailable" }, presenter.CsrfToken))
                await AssertErrorAsync(unknownSource, (HttpStatusCode)422, "unknown_source",
                    "Source is not one of the five case sources.");
            using (var invalidMode = await PostModeAsync(client, presenter, "technical",
                new { commandId = "m03-accepted", expectedRevision = before.Revision, mode = "unknown" }, presenter.CsrfToken))
                await AssertErrorAsync(invalidMode, (HttpStatusCode)422, "invalid_mode",
                    "Mode must be available, unavailable, stale or conflicting.");
            using (var stale = await PostModeAsync(client, presenter, "records",
                new { commandId = "m03-stale", expectedRevision = before.Revision, mode = "unavailable" }, presenter.CsrfToken))
                await AssertErrorAsync(stale, HttpStatusCode.Conflict, "stale_revision",
                    "The case revision has changed; reload and retry.");
            using (var unchanged = await PostModeAsync(client, presenter, "technical",
                new { commandId = "m03-unchanged", expectedRevision = acceptedState.Revision, mode = "unavailable" }, presenter.CsrfToken))
                await AssertErrorAsync(unchanged, (HttpStatusCode)422, "mode_unchanged", "The source already has this mode.");

            var afterRejections = cases.Read(scope);
            Assert.Equal(acceptedState.Revision, afterRejections.Revision);
            Assert.Equal(acceptedState.Audit.Count, afterRejections.Audit.Count);
            Assert.Equal(acceptedState.Audit.Count(a => a.Action == SourceModes.Action),
                afterRejections.Audit.Count(a => a.Action == SourceModes.Action));
            Assert.Equal(acceptedModes, ModeText(modeStore, afterRejections));

            raceStore.ChangeRevisionOnNextCommit = true;
            using (var raced = await PostModeAsync(client, presenter, "maintenance",
                new { commandId = "m03-race", expectedRevision = afterRejections.Revision, mode = "stale" }, presenter.CsrfToken))
                await AssertErrorAsync(raced, HttpStatusCode.Conflict, "stale_revision",
                    "The case revision has changed; reload and retry.");

            var afterRace = cases.Read(scope);
            Assert.Equal(afterRejections.Revision + 1, afterRace.Revision);
            Assert.Equal(afterRejections.Audit.Count + 1, afterRace.Audit.Count);
            Assert.Equal(1, afterRace.Audit.Count(a => a.Action == SourceModes.Action));
            Assert.Equal(acceptedModes, ModeText(modeStore, afterRace));
            Assert.DoesNotContain(afterRace.Audit, a => a.Action == SourceModes.Action && a.Detail.Contains("m03-race", StringComparison.Ordinal));

            var caseBoundCommandId = "m03-case-bound";
            var caseBoundExpectedRevision = afterRace.Revision + 1;
            var mismatchedCaseDetail = JsonSerializer.Serialize(new
            {
                commandId = caseBoundCommandId,
                caseId = "case-002",
                sourceId = "technical",
                priorMode = "available",
                newMode = "unavailable",
                priorRevision = caseBoundExpectedRevision,
                newRevision = caseBoundExpectedRevision + 1,
            });
            // Seed a different persisted case identity to verify it cannot replay this command.
            cases.Commit(scope, afterRace.Revision, new CaseCommit(null, SourceModes.ReceiptPrefix + caseBoundCommandId,
                Presenter.Id, SourceModes.Action, mismatchedCaseDetail, Now));
            var beforeCaseMismatch = cases.Read(scope);
            Assert.Equal(caseBoundExpectedRevision, beforeCaseMismatch.Revision);
            using (var otherCase = await PostModeAsync(client, presenter, "technical",
                new { commandId = caseBoundCommandId, expectedRevision = caseBoundExpectedRevision, mode = "unavailable" },
                presenter.CsrfToken))
                await AssertErrorAsync(otherCase, HttpStatusCode.Conflict, "command_id_conflict",
                    "This command ID was already used for a different command.");

            var afterCaseMismatch = cases.Read(scope);
            Assert.Equal(beforeCaseMismatch.Revision, afterCaseMismatch.Revision);
            Assert.Equal(beforeCaseMismatch.Audit.Count, afterCaseMismatch.Audit.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Command_id_over_64_characters_is_rejected_and_64_is_accepted()
    {
        var path = TempStatePath();
        try
        {
            await using var factory = CreateFactory(path);
            var cases = Cases(factory);
            var scope = Ensure(cases);
            var before = cases.Read(scope);
            var session = Session(factory, Presenter);
            using var client = factory.CreateClient();

            using (var tooLong = await PostModeAsync(client, session, "technical",
                new { commandId = new string('c', 65), expectedRevision = before.Revision, mode = "unavailable" }, session.CsrfToken))
            {
                await AssertErrorAsync(tooLong, HttpStatusCode.BadRequest, "invalid_request",
                    "commandId must be at most 64 characters.");
            }

            var unchanged = cases.Read(scope);
            Assert.Equal(before.Revision, unchanged.Revision);
            Assert.Equal(before.Audit.Count, unchanged.Audit.Count);

            using var ok = await PostModeAsync(client, session, "technical",
                new { commandId = new string('c', 64), expectedRevision = before.Revision, mode = "unavailable" }, session.CsrfToken);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Production_registrations_resolve_SourceModeStore_with_system_TimeProvider()
    {
        var path = TempStatePath();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Demo:Passcode"] = Passcode,
                    ["CaseState:Path"] = path,
                })));
        try
        {
            using var scope = factory.Services.CreateScope();
            Assert.Same(TimeProvider.System, scope.ServiceProvider.GetRequiredService<TimeProvider>());
            Assert.NotNull(scope.ServiceProvider.GetRequiredService<SourceModeStore>());
        }
        finally
        {
            File.Delete(path);
        }
    }
}

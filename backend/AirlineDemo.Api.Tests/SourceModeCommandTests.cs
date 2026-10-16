using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Api.Runs;
using AirlineDemo.Api.SourceSystems;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AirlineDemo.Api.Tests;

public sealed class SourceModeCommandTests
{
    private const string Passcode = "test-passcode";
    private const string Case = "case-001";
    private const string Path = "/api/v1/cases/case-001/sources/{0}/mode";
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly DemoUser Presenter = new("presenter", "Presenter", "presenter", ["case-001", "case-002"]);
    private static readonly DemoUser OtherPresenter = new("presenter-2", "Second presenter", "presenter", ["case-001"]);

    private static WebApplicationFactory<Program> CreateFactory(string statePath) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Demo:Passcode"] = Passcode,
                    ["CaseState:Path"] = statePath,
                }));
            builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(new FixedTime(Now)));
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

    private static Task<HttpResponseMessage> PostModeAsync(HttpClient client, DemoSession? session, string source, object body, string? csrf)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, string.Format(Path, source)) { Content = JsonContent.Create(body) };
        if (session is not null) request.Headers.Add("Cookie", $"__Host-demo-session={session.Id}");
        if (csrf is not null) request.Headers.Add("X-CSRF-Token", csrf);
        return client.SendAsync(request);
    }

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
                Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
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
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            }

            var outsider = Session(factory, new DemoUser("presenter-3", "Other case", "presenter", ["case-002"]));
            using (var outOfScope = await PostModeAsync(client, outsider, "technical", body, outsider.CsrfToken))
            {
                Assert.Equal(HttpStatusCode.NotFound, outOfScope.StatusCode);
            }

            using (var missingCsrf = await PostModeAsync(client, presenter, "technical", body, null))
            {
                Assert.Equal(HttpStatusCode.Forbidden, missingCsrf.StatusCode);
            }

            using (var wrongCsrf = await PostModeAsync(client, presenter, "technical", body, "not-the-token"))
            {
                Assert.Equal(HttpStatusCode.Forbidden, wrongCsrf.StatusCode);
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
                Assert.Equal(HttpStatusCode.Conflict, otherActor.StatusCode);
                var error = await otherActor.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("command_id_conflict", error.GetProperty("error").GetProperty("code").GetString());
            }

            using (var otherPayload = await PostModeAsync(client, presenter, "finance", new { commandId = "cmd-replay", expectedRevision = snapshotRevision, mode = "conflicting" }, presenter.CsrfToken))
            {
                Assert.Equal(HttpStatusCode.Conflict, otherPayload.StatusCode);
                var error = await otherPayload.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("command_id_conflict", error.GetProperty("error").GetProperty("code").GetString());
            }

            using (var stale = await PostModeAsync(client, presenter, "records", new { commandId = "cmd-stale", expectedRevision = start.Revision, mode = "unavailable" }, presenter.CsrfToken))
            {
                Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
                var error = await stale.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("stale_revision", error.GetProperty("error").GetProperty("code").GetString());
            }

            using (var unchanged = await PostModeAsync(client, presenter, "finance", new { commandId = "cmd-same", expectedRevision = snapshotRevision, mode = "stale" }, presenter.CsrfToken))
            {
                Assert.Equal((HttpStatusCode)422, unchanged.StatusCode);
                var error = await unchanged.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("mode_unchanged", error.GetProperty("error").GetProperty("code").GetString());
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
}

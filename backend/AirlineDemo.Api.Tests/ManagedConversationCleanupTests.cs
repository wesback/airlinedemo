using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using AirlineDemo.Api.Persistence;
using AirlineDemo.Deployment;
using Azure.Core;
using Microsoft.EntityFrameworkCore;

namespace AirlineDemo.Api.Tests;

public sealed class ManagedConversationCleanupTests
{
    [Fact]
    public async Task CleanupClientUsesOnlyTheDirectProjectApiAndValidatesDeleteReceipt()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var credential = new StaticTokenCredential();
        var client = new ManagedCleanupClient(
            new Uri("https://demo.services.ai.azure.com/api/projects/airline-demo"), http, credential);

        await client.ListConversationsAsync(null, TestContext.Current.CancellationToken);
        await client.ListItemsAsync("conv_1", "item_1", TestContext.Current.CancellationToken);
        await client.ListResponsesAsync("conv_1", "resp_1", TestContext.Current.CancellationToken);
        await client.DeleteItemAsync("conv_1", "item_1", TestContext.Current.CancellationToken);
        await client.DeleteResponseAsync("resp_1", TestContext.Current.CancellationToken);
        Assert.True(await client.DeleteConversationAsync("conv_1", TestContext.Current.CancellationToken));
        Assert.False(await client.ConversationExistsAsync("conv_1", TestContext.Current.CancellationToken));

        Assert.Equal([
            "GET /api/projects/airline-demo/openai/v1/conversations?limit=100&order=asc",
            "GET /api/projects/airline-demo/openai/v1/conversations/conv_1/items?limit=100&order=asc&after=item_1",
            "GET /api/projects/airline-demo/openai/v1/responses?limit=100&order=asc&conversation_id=conv_1&after=resp_1",
            "DELETE /api/projects/airline-demo/openai/v1/conversations/conv_1/items/item_1",
            "DELETE /api/projects/airline-demo/openai/v1/responses/resp_1",
            "DELETE /api/projects/airline-demo/openai/v1/conversations/conv_1",
            "GET /api/projects/airline-demo/openai/v1/conversations/conv_1",
        ], handler.Requests);
        Assert.All(handler.Authorizations, value => Assert.Equal("Bearer cleanup-token", value));
        Assert.Equal(7, credential.Scopes.Count);
        Assert.All(credential.Scopes, scope => Assert.Equal("https://ai.azure.com/.default", scope));
    }

    [Fact]
    public async Task CleanupClientUsesAbsenceReadWhenDeleteHasNoReceiptBody()
    {
        var handler = new RecordingHandler { EmptyDeleteBodies = true };
        using var http = new HttpClient(handler);
        var client = new ManagedCleanupClient(
            new Uri("https://demo.services.ai.azure.com/api/projects/airline-demo"), http,
            new StaticTokenCredential());

        await client.DeleteItemAsync("conv_1", "item_1", TestContext.Current.CancellationToken);
        await client.DeleteResponseAsync("resp_1", TestContext.Current.CancellationToken);
        Assert.False(await client.DeleteConversationAsync("conv_1", TestContext.Current.CancellationToken));
        Assert.False(await client.ConversationExistsAsync("conv_1", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("https://gateway.example/foundry/projects/airline-demo")]
    [InlineData("https://outside.example/api/projects/airline-demo")]
    [InlineData("https://demo.services.ai.azure.com/openai/v1")]
    [InlineData("https://demo.services.ai.azure.com/api/projects/airline-demo?api-version=1")]
    public void CleanupClientRejectsNonProjectEndpoints(string endpoint)
    {
        using var http = new HttpClient(new RecordingHandler());
        Assert.Throws<ArgumentException>(() => new ManagedCleanupClient(
            new Uri(endpoint), http, new StaticTokenCredential()));
    }

    [Fact]
    public async Task CleanupClaimsAreAtomicAcrossWorkersAndExpireByLease()
    {
        using var fixture = new ManagedConversationStoreTests.SqliteFixture();
        var (tuple, creating) = ReserveUnknownId(fixture, "case-atomic");
        fixture.Clock.Advance(SqlManagedConversationStore.ConversationLifetime);
        var firstStore = fixture.Store();
        var secondStore = fixture.Store();
        using var barrier = new Barrier(2);

        var results = await Task.WhenAll(
            Task.Run(() => { barrier.SignalAndWait(); return firstStore.ClaimManagedCleanupBatch(); }),
            Task.Run(() => { barrier.SignalAndWait(); return secondStore.ClaimManagedCleanupBatch(); }));

        var claim = Assert.Single(results.SelectMany(result => result));
        Assert.Equal(creating.AttemptId, claim.AttemptId);
        Assert.Null(claim.RemoteConversationId);
        Assert.Equal(tuple, claim.Tuple);
        Assert.Empty(secondStore.ClaimManagedCleanupBatch());

        fixture.Clock.Advance(SqlManagedConversationStore.CleanupLeaseDuration);
        var replacement = Assert.Single(secondStore.ClaimManagedCleanupBatch());
        Assert.NotEqual(claim.ClaimToken, replacement.ClaimToken);
        Assert.False(firstStore.ReleaseManagedCleanup(claim));
    }

    [Fact]
    public void CleanupClaimsLimitBindingsAndRollingAttempts()
    {
        using var fixture = new ManagedConversationStoreTests.SqliteFixture();
        var store = fixture.Store();
        for (var index = 0; index < 101; index++)
            ReserveUnknownId(fixture, $"case-limit-{index}");
        fixture.Clock.Advance(SqlManagedConversationStore.ConversationLifetime);

        var batch = store.ClaimManagedCleanupBatch();

        Assert.Equal(100, batch.Count);
        foreach (var claim in batch)
            Assert.True(store.ReleaseManagedCleanup(claim));

        using var retryFixture = new ManagedConversationStoreTests.SqliteFixture();
        var retryStore = retryFixture.Store();
        ReserveUnknownId(retryFixture, "case-retry");
        retryFixture.Clock.Advance(SqlManagedConversationStore.ConversationLifetime);
        for (var attempt = 0; attempt < SqlManagedConversationStore.MaximumCleanupAttempts; attempt++)
        {
            var claim = Assert.Single(retryStore.ClaimManagedCleanupBatch());
            Assert.True(retryStore.ReleaseManagedCleanup(claim));
        }
        Assert.Empty(retryStore.ClaimManagedCleanupBatch());

        retryFixture.Clock.Advance(SqlManagedConversationStore.CleanupAttemptWindow);
        Assert.Single(retryStore.ClaimManagedCleanupBatch());
    }

    [Fact]
    public void ConfirmedCleanupMetadataIsRetainedForSevenDays()
    {
        using var fixture = new ManagedConversationStoreTests.SqliteFixture();
        var store = fixture.Store();
        var tuple = Tuple("case-retention");
        var reserved = store.ReserveManagedConversation(tuple);
        Assert.True(store.RetireManagedConversation(reserved.Handle, tuple, reserved.RowRevision, null));
        fixture.Clock.Advance(SqlManagedConversationStore.ConversationLifetime);
        var claim = Assert.Single(store.ClaimManagedCleanupBatch());

        Assert.True(store.ConfirmManagedCleanup(claim));
        fixture.Clock.Advance(SqlManagedConversationStore.CleanupMetadataRetention - TimeSpan.FromTicks(1));
        Assert.Equal(0, store.PurgeConfirmedManagedConversations());
        fixture.Clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(1, store.PurgeConfirmedManagedConversations());
        Assert.Null(store.ReadManagedConversation(reserved.Handle));
    }

    [Fact]
    public async Task CleanupDeletesChildrenBeforeConversationAndConfirmsAbsence()
    {
        using var fixture = new ManagedConversationStoreTests.SqliteFixture();
        var store = fixture.Store();
        var snapshot = ReserveKnownId(fixture, "case-cleanup", "conv-cleanup");
        var remote = new FakeManagedCleanupRemote();
        remote.AddConversation(snapshot.RemoteConversationId!, snapshot.Handle, snapshot.AttemptId,
            ["item-1", "item-2"], ["resp-1"]);
        var cleanup = new ManagedConversationCleanup(store, remote);

        var summary = await cleanup.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, summary.Claimed);
        Assert.Equal(4, summary.Deleted);
        Assert.Equal(1, summary.Confirmed);
        Assert.Equal(0, summary.Purged);
        Assert.Empty(summary.FailureCodes);
        Assert.False(await remote.ConversationExistsAsync("conv-cleanup", TestContext.Current.CancellationToken));
        using var db = fixture.NewContext();
        var row = Assert.Single(db.ManagedConversations);
        Assert.NotNull(row.CleanupConfirmedAtTicks);
        Assert.Equal(["item-1", "item-2"], JsonNode.Parse(row.CleanupDeletedItemIdsJson)!.AsArray()
            .Select(node => node!.GetValue<string>()));
        Assert.Equal(["resp-1"], JsonNode.Parse(row.CleanupDeletedResponseIdsJson)!.AsArray()
            .Select(node => node!.GetValue<string>()));
        Assert.True(row.CleanupConversationDeleted);
        Assert.Equal(["resp-1"], remote.DeletedResponses);
        Assert.Equal(["item-1", "item-2"], remote.DeletedItems);
    }

    [Fact]
    public async Task ExpiredHistoryIsDeniedBeforeServiceCleanupRuns()
    {
        using var fixture = new ManagedConversationStoreTests.SqliteFixture();
        var store = fixture.Store();
        var tuple = Tuple("case-expired");
        var reserved = store.ReserveManagedConversation(tuple);
        var creating = store.BeginRemoteCreate(reserved.Handle, tuple, reserved.RowRevision);
        var attached = store.AttachRemoteConversationId(
            reserved.Handle, tuple, reserved.AttemptId, "conv-expired", creating.RowRevision);
        fixture.Clock.Advance(SqlManagedConversationStore.ConversationLifetime);
        var denied = Assert.Throws<ManagedConversationStoreException>(() =>
            store.AdmitManagedTurn(attached.Handle, tuple, attached.RowRevision, tuple.CaseRevision));
        Assert.Equal("conversation_expired", denied.Code);

        var remote = new FakeManagedCleanupRemote();
        remote.AddConversation(attached.RemoteConversationId!, attached.Handle, attached.AttemptId, [], []);
        var summary = await new ManagedConversationCleanup(store, remote)
            .RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, summary.Confirmed);
        Assert.False(remote.HasConversation("conv-expired"));
    }

    [Fact]
    public async Task CleanupResumesAfterAChildDeleteFailsMidBatch()
    {
        using var fixture = new ManagedConversationStoreTests.SqliteFixture();
        var store = fixture.Store();
        var snapshot = ReserveKnownId(fixture, "case-resume", "conv-resume");
        var remote = new FakeManagedCleanupRemote { FailOnItemDeleteNumber = 2 };
        remote.AddConversation(snapshot.RemoteConversationId!, snapshot.Handle, snapshot.AttemptId,
            ["item-1", "item-2"], []);
        var cleanup = new ManagedConversationCleanup(store, remote);

        var first = await cleanup.RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal("managed_cleanup_remote_unavailable", Assert.Single(first.FailureCodes));
        Assert.Equal(["item-2"], remote.Items("conv-resume"));
        using (var db = fixture.NewContext())
            Assert.Null(Assert.Single(db.ManagedConversations).CleanupConfirmedAtTicks);

        remote.FailOnItemDeleteNumber = null;
        var resumed = await cleanup.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, resumed.Confirmed);
        Assert.False(remote.HasConversation("conv-resume"));
        Assert.Equal(["item-1", "item-2"], remote.DeletedItems);
    }

    [Fact]
    public async Task CleanupAmbiguousDeleteDoesNotCheckpointOrConfirm()
    {
        using var fixture = new ManagedConversationStoreTests.SqliteFixture();
        var store = fixture.Store();
        var snapshot = ReserveKnownId(fixture, "case-ambiguous", "conv-ambiguous");
        var remote = new FakeManagedCleanupRemote { RetainDeletedItems = true };
        remote.AddConversation(snapshot.RemoteConversationId!, snapshot.Handle, snapshot.AttemptId, ["item-1"], []);
        var cleanup = new ManagedConversationCleanup(store, remote);

        var summary = await cleanup.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, summary.Failed);
        Assert.Equal("managed_cleanup_absence_unconfirmed", Assert.Single(summary.FailureCodes));
        Assert.Equal(["item-1"], remote.Items("conv-ambiguous"));
        using var db = fixture.NewContext();
        var row = Assert.Single(db.ManagedConversations);
        Assert.Equal("[]", row.CleanupDeletedItemIdsJson);
        Assert.Null(row.CleanupConfirmedAtTicks);
    }

    [Fact]
    public async Task CleanupReconcilesUnknownRemoteIdByExactReservationAndAttempt()
    {
        using var fixture = new ManagedConversationStoreTests.SqliteFixture();
        var (tuple, creating) = ReserveUnknownId(fixture, "case-recovery");
        fixture.Clock.Advance(SqlManagedConversationStore.ConversationLifetime);
        var remote = new FakeManagedCleanupRemote();
        remote.AddConversation("conv-recovered", creating.Handle, creating.AttemptId, ["item-recovered"], []);
        var cleanup = new ManagedConversationCleanup(fixture.Store(), remote);

        var summary = await cleanup.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, summary.Confirmed);
        Assert.Equal(1, summary.RecoveryRequests);
        Assert.Equal(["item-recovered"], remote.DeletedItems);
        Assert.False(remote.HasConversation("conv-recovered"));
        Assert.Equal(tuple, fixture.Store().ReadManagedConversation(creating.Handle)!.Tuple);
    }

    [Fact]
    public async Task CleanupKeepsAmbiguousRecoveryTombstone()
    {
        using var fixture = new ManagedConversationStoreTests.SqliteFixture();
        var (_, creating) = ReserveUnknownId(fixture, "case-ambiguous-recovery");
        fixture.Clock.Advance(SqlManagedConversationStore.ConversationLifetime);
        var remote = new FakeManagedCleanupRemote();
        remote.AddConversation("conv-one", creating.Handle, creating.AttemptId, [], []);
        remote.AddConversation("conv-two", creating.Handle, creating.AttemptId, [], []);
        var cleanup = new ManagedConversationCleanup(fixture.Store(), remote);

        var summary = await cleanup.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal("managed_cleanup_recovery_unresolved", Assert.Single(summary.FailureCodes));
        Assert.True(remote.HasConversation("conv-one"));
        Assert.True(remote.HasConversation("conv-two"));
        Assert.Null(fixture.Store().ReadManagedConversation(creating.Handle)!.RemoteConversationId);
    }

    [Fact]
    public async Task CleanupEnforcesOneThousandDeleteCapAndStopsOnCancellation()
    {
        using var fixture = new ManagedConversationStoreTests.SqliteFixture();
        var store = fixture.Store();
        var snapshot = ReserveKnownId(fixture, "case-delete-cap", "conv-delete-cap");
        var remote = new FakeManagedCleanupRemote();
        remote.AddConversation(snapshot.RemoteConversationId!, snapshot.Handle, snapshot.AttemptId,
            Enumerable.Range(0, 1001).Select(index => $"item-{index}").ToArray(), []);
        var cleanup = new ManagedConversationCleanup(store, remote);

        var first = await cleanup.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1000, first.Deleted);
        Assert.Equal("managed_cleanup_run_limit", Assert.Single(first.FailureCodes));
        Assert.Single(remote.Items("conv-delete-cap"));
        Assert.Equal(TimeSpan.FromMinutes(10), ManagedConversationCleanup.MaximumRunDuration);

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var second = await cleanup.RunAsync(canceled.Token);
        Assert.Equal(0, second.Deleted);
    }

    [Fact]
    public async Task CleanupCancellationStopsBeforeCheckpointOrConversationDeletion()
    {
        using var fixture = new ManagedConversationStoreTests.SqliteFixture();
        var store = fixture.Store();
        var snapshot = ReserveKnownId(fixture, "case-deadline", "conv-deadline");
        using var cancellation = new CancellationTokenSource();
        var remote = new FakeManagedCleanupRemote { CancelOnItemDelete = cancellation };
        remote.AddConversation(snapshot.RemoteConversationId!, snapshot.Handle, snapshot.AttemptId, ["item-1"], []);
        var cleanup = new ManagedConversationCleanup(store, remote);

        var summary = await cleanup.RunAsync(cancellation.Token);

        Assert.Equal(1, summary.Deleted);
        Assert.Equal(0, summary.Confirmed);
        Assert.True(remote.HasConversation("conv-deadline"));
        using var db = fixture.NewContext();
        var row = Assert.Single(db.ManagedConversations);
        Assert.Equal("[]", row.CleanupDeletedItemIdsJson);
        Assert.Null(row.CleanupConfirmedAtTicks);
    }

    private static ManagedConversationSnapshot ReserveKnownId(
        ManagedConversationStoreTests.SqliteFixture fixture, string caseId, string remoteId)
    {
        var store = fixture.Store();
        var tuple = Tuple(caseId);
        var reserved = store.ReserveManagedConversation(tuple);
        var creating = store.BeginRemoteCreate(reserved.Handle, tuple, reserved.RowRevision);
        var attached = store.AttachRemoteConversationId(
            reserved.Handle, tuple, reserved.AttemptId, remoteId, creating.RowRevision);
        Assert.True(store.RetireManagedConversation(attached.Handle, tuple, attached.RowRevision, null));
        return store.ReadManagedConversation(attached.Handle)!;
    }

    private static (ManagedConversationTuple Tuple, ManagedConversationSnapshot Creating) ReserveUnknownId(
        ManagedConversationStoreTests.SqliteFixture fixture, string caseId)
    {
        var store = fixture.Store();
        var tuple = Tuple(caseId);
        var reserved = store.ReserveManagedConversation(tuple);
        var creating = store.BeginRemoteCreate(reserved.Handle, tuple, reserved.RowRevision);
        Assert.True(store.RetireManagedConversation(creating.Handle, tuple, creating.RowRevision, null));
        return (tuple, store.ReadManagedConversation(creating.Handle)!);
    }

    private static ManagedConversationTuple Tuple(string caseId) =>
        new("demo-user-1", "presenter", caseId, "entitlements-digest", 0,
            "case-agent", "7", "configuration-digest");

    private sealed class FakeManagedCleanupRemote : IManagedCleanupRemote
    {
        private readonly Dictionary<string, FakeConversation> _conversations = new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<string> _deletedItems = new();
        private readonly ConcurrentQueue<string> _deletedResponses = new();
        public bool RetainDeletedItems { get; init; }
        public int? FailOnItemDeleteNumber { get; set; }
        public CancellationTokenSource? CancelOnItemDelete { get; init; }
        public IReadOnlyList<string> DeletedItems => _deletedItems.ToArray();
        public IReadOnlyList<string> DeletedResponses => _deletedResponses.ToArray();
        private int _itemDeleteCalls;

        public void AddConversation(
            string id, string reservationId, string attemptId, IReadOnlyList<string> items,
            IReadOnlyList<string> responses) =>
            _conversations.Add(id, new FakeConversation(reservationId, attemptId, [.. items], [.. responses]));

        public IReadOnlyList<string> Items(string id) => _conversations[id].Items.ToArray();
        public IReadOnlyList<string> Responses(string id) => _conversations[id].Responses.ToArray();
        public bool HasConversation(string id) => _conversations.ContainsKey(id);

        public Task<JsonObject> ListConversationsAsync(string? after, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = _conversations.Select(pair => new JsonObject
            {
                ["id"] = pair.Key,
                ["metadata"] = new JsonObject
                {
                    ["reservation_id"] = pair.Value.ReservationId,
                    ["attempt_id"] = pair.Value.AttemptId,
                },
            }).ToArray();
            return Task.FromResult(Page(rows, after));
        }

        public Task<JsonObject> ListItemsAsync(string conversationId, string? after, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Page(_conversations[conversationId].Items
                .Select(id => (JsonObject)new JsonObject { ["id"] = id }).ToArray(), after));
        }

        public Task<JsonObject> ListResponsesAsync(string conversationId, string? after, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Page(_conversations[conversationId].Responses
                .Select(id => (JsonObject)new JsonObject { ["id"] = id }).ToArray(), after));
        }

        public Task DeleteItemAsync(string conversationId, string itemId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailOnItemDeleteNumber == ++_itemDeleteCalls)
                throw new ManagedCleanupException("managed_cleanup_remote_unavailable");
            _deletedItems.Enqueue(itemId);
            if (!RetainDeletedItems)
                _conversations[conversationId].Items.Remove(itemId);
            CancelOnItemDelete?.Cancel();
            return Task.CompletedTask;
        }

        public Task DeleteResponseAsync(string responseId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _deletedResponses.Enqueue(responseId);
            foreach (var conversation in _conversations.Values)
                conversation.Responses.Remove(responseId);
            return Task.CompletedTask;
        }

        public Task<bool> DeleteConversationAsync(string conversationId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _conversations.Remove(conversationId);
            return Task.FromResult(true);
        }

        public Task<bool> ConversationExistsAsync(string conversationId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_conversations.ContainsKey(conversationId));
        }

        private static JsonObject Page(JsonObject[] all, string? after)
        {
            var offset = after is null ? 0 : Array.FindIndex(all, row => row["id"]?.GetValue<string>() == after) + 1;
            var data = all.Skip(offset).Take(100).ToArray();
            var more = offset + data.Length < all.Length;
            return new JsonObject
            {
                ["data"] = new JsonArray(data.Select(row => row.DeepClone()).ToArray()),
                ["has_more"] = more,
                ["last_id"] = data.LastOrDefault()?["id"]?.GetValue<string>(),
            };
        }

        private sealed record FakeConversation(
            string ReservationId, string AttemptId, List<string> Items, List<string> Responses);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public List<string?> Authorizations { get; } = [];
        public bool EmptyDeleteBodies { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.RequestUri!;
            Requests.Add($"{request.Method} {uri.PathAndQuery}");
            Authorizations.Add(request.Headers.Authorization?.ToString());
            if (request.Method == HttpMethod.Get
                && uri.AbsolutePath.EndsWith("/conversations/conv_1", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (EmptyDeleteBodies && request.Method == HttpMethod.Delete)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            var body = request.Method == HttpMethod.Delete && uri.AbsolutePath.EndsWith("/conversations/conv_1", StringComparison.Ordinal)
                ? """{"id":"conv_1","object":"conversation.deleted","deleted":true}"""
                : """{"data":[],"has_more":false}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body),
            });
        }
    }

    private sealed class StaticTokenCredential : TokenCredential
    {
        public List<string> Scopes { get; } = [];

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Scopes.AddRange(requestContext.Scopes);
            return new AccessToken("cleanup-token", DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Scopes.AddRange(requestContext.Scopes);
            return ValueTask.FromResult(new AccessToken("cleanup-token", DateTimeOffset.UtcNow.AddHours(1)));
        }
    }
}

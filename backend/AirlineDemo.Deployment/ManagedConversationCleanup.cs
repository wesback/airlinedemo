using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AirlineDemo.Api.Persistence;
using Azure.Core;
using Azure.Identity;
using Microsoft.EntityFrameworkCore;

namespace AirlineDemo.Deployment;

public interface IManagedCleanupRemote
{
    Task<JsonObject> ListConversationsAsync(string? after, CancellationToken cancellationToken);
    Task<JsonObject> ListItemsAsync(string conversationId, string? after, CancellationToken cancellationToken);
    Task<JsonObject> ListResponsesAsync(string conversationId, string? after, CancellationToken cancellationToken);
    Task DeleteItemAsync(string conversationId, string itemId, CancellationToken cancellationToken);
    Task DeleteResponseAsync(string responseId, CancellationToken cancellationToken);
    Task<bool> DeleteConversationAsync(string conversationId, CancellationToken cancellationToken);
    Task<bool> ConversationExistsAsync(string conversationId, CancellationToken cancellationToken);
}

public sealed class ManagedCleanupException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public sealed class ManagedCleanupClient : IManagedCleanupRemote
{
    private const string Scope = "https://ai.azure.com/.default";
    private static readonly Regex Identifier = new("^[A-Za-z0-9_-]{1,128}\\z", RegexOptions.CultureInvariant);
    private readonly Uri _projectEndpoint;
    private readonly HttpClient _client;
    private readonly TokenCredential _credential;

    public ManagedCleanupClient(Uri projectEndpoint, HttpClient client, TokenCredential credential)
    {
        if (projectEndpoint.Scheme != "https" || projectEndpoint.UserInfo.Length != 0
            || projectEndpoint.Query.Length != 0 || projectEndpoint.Fragment.Length != 0
            || !projectEndpoint.Host.EndsWith(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase)
            || !Regex.IsMatch(projectEndpoint.AbsolutePath, "^/api/projects/[A-Za-z0-9_-]{1,128}/?$",
                RegexOptions.CultureInvariant))
            throw new ArgumentException("Expected a direct HTTPS Foundry project endpoint.", nameof(projectEndpoint));
        _projectEndpoint = new Uri(projectEndpoint.AbsoluteUri.TrimEnd('/'));
        _client = client;
        _credential = credential;
    }

    public Task<JsonObject> ListConversationsAsync(string? after, CancellationToken cancellationToken) =>
        GetAsync("conversations?limit=100&order=asc" + Cursor(after), cancellationToken);

    public Task<JsonObject> ListItemsAsync(string conversationId, string? after, CancellationToken cancellationToken) =>
        GetAsync($"conversations/{Id(conversationId)}/items?limit=100&order=asc{Cursor(after)}", cancellationToken);

    public Task<JsonObject> ListResponsesAsync(string conversationId, string? after, CancellationToken cancellationToken) =>
        GetAsync($"responses?limit=100&order=asc&conversation_id={Id(conversationId)}{Cursor(after)}", cancellationToken);

    public async Task DeleteItemAsync(string conversationId, string itemId, CancellationToken cancellationToken)
    {
        _ = await SendAsync(HttpMethod.Delete,
            $"conversations/{Id(conversationId)}/items/{Id(itemId)}", cancellationToken,
            allowNotFound: true, allowEmptyBody: true);
    }

    public async Task DeleteResponseAsync(string responseId, CancellationToken cancellationToken)
    {
        _ = await SendAsync(HttpMethod.Delete, $"responses/{Id(responseId)}",
            cancellationToken, allowNotFound: true, allowEmptyBody: true);
    }

    public async Task<bool> DeleteConversationAsync(string conversationId, CancellationToken cancellationToken)
    {
        var result = await SendAsync(HttpMethod.Delete, $"conversations/{Id(conversationId)}",
            cancellationToken, allowNotFound: true, allowEmptyBody: true);
        return result is not null
            && result["id"]?.GetValue<string>() == conversationId
            && result["object"]?.GetValue<string>() == "conversation.deleted"
            && result["deleted"]?.GetValue<bool>() == true;
    }

    public async Task<bool> ConversationExistsAsync(string conversationId, CancellationToken cancellationToken) =>
        await SendAsync(HttpMethod.Get, $"conversations/{Id(conversationId)}",
            cancellationToken, allowNotFound: true) is not null;

    private async Task<JsonObject> GetAsync(string path, CancellationToken cancellationToken) =>
        await SendAsync(HttpMethod.Get, path, cancellationToken, allowNotFound: false)
        ?? throw new ManagedCleanupException("managed_cleanup_service_contract_invalid");

    private async Task<JsonObject?> SendAsync(
        HttpMethod method, string path, CancellationToken cancellationToken,
        bool allowNotFound, bool allowEmptyBody = false)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var token = await _credential.GetTokenAsync(new TokenRequestContext([Scope]), timeout.Token);
            using var request = new HttpRequestMessage(method,
                _projectEndpoint.AbsoluteUri.TrimEnd('/') + "/openai/v1/" + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)
                return null;
            if (!response.IsSuccessStatusCode)
            {
                var code = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? "managed_cleanup_not_authorized"
                    : "managed_cleanup_remote_unavailable";
                throw new ManagedCleanupException(code);
            }

            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (allowEmptyBody && string.IsNullOrWhiteSpace(body))
                return new JsonObject();
            return JsonNode.Parse(body,
                    documentOptions: new JsonDocumentOptions { AllowDuplicateProperties = false }) as JsonObject
                ?? throw new JsonException();
        }
        catch (ManagedCleanupException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException
            or JsonException or AuthenticationFailedException or InvalidOperationException)
        {
            throw new ManagedCleanupException("managed_cleanup_remote_unavailable");
        }
    }

    private static string Cursor(string? after) => after is null ? "" : $"&after={Id(after)}";

    private static string Id(string value) =>
        Identifier.IsMatch(value) ? value : throw new ManagedCleanupException("managed_cleanup_service_contract_invalid");
}

public sealed record ManagedCleanupSummary(
    int Claimed, int Deleted, int Confirmed, int Purged, int RecoveryRequests,
    IReadOnlyList<string> FailureCodes)
{
    public int Failed => FailureCodes.Count;
}

public sealed class ManagedConversationCleanup(
    IManagedConversationStore store,
    IManagedCleanupRemote remote)
{
    public static readonly TimeSpan MaximumRunDuration = TimeSpan.FromMinutes(10);
    public const int MaximumDeletesPerRun = 1000;

    public async Task<ManagedCleanupSummary> RunAsync(CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(MaximumRunDuration);
        var purged = store.PurgeConfirmedManagedConversations();
        var remaining = SqlManagedConversationStore.MaximumManagedCleanupClaims - purged;
        var claims = remaining == 0 ? [] : store.ClaimManagedCleanupBatch(remaining);
        var deleted = 0;
        var confirmed = 0;
        var recoveryRequests = 0;
        var failures = new List<string>();
        foreach (var original in claims)
        {
            var claim = original;
            var finished = false;
            try
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (claim.RemoteConversationId is null && claim.CreateAttemptCount == 0)
                {
                    finished = store.ConfirmManagedCleanup(claim);
                }
                else
                {
                    if (claim.RemoteConversationId is null)
                    {
                        var recovered = await ManagedCreateRecovery.ResolveConversationAsync(
                            claim.Handle, claim.AttemptId, remote.ListConversationsAsync,
                            () =>
                            {
                                if (deadline.IsCancellationRequested)
                                    return false;
                                recoveryRequests++;
                                return true;
                            }, deadline.Token);
                        if (recovered is null)
                            throw new ManagedCleanupException("managed_cleanup_recovery_unresolved");
                        claim = store.AttachRecoveredManagedCleanupRemoteId(claim, recovered);
                    }

                    if (!claim.ConversationDeleted)
                    {
                        var conversationId = claim.RemoteConversationId!;
                        claim = await DeleteResponsesAsync(claim, conversationId, deleted, deadline.Token,
                            count => deleted = count);
                        claim = await DeleteItemsAsync(claim, conversationId, deleted, deadline.Token,
                            count => deleted = count);
                        var exists = await remote.ConversationExistsAsync(conversationId, deadline.Token);
                        if (exists)
                        {
                            if (deleted >= MaximumDeletesPerRun)
                                throw new ManagedCleanupException("managed_cleanup_run_limit");
                            _ = await remote.DeleteConversationAsync(conversationId, deadline.Token);
                            deleted++;
                            exists = await remote.ConversationExistsAsync(conversationId, deadline.Token);
                        }
                        if (exists)
                            throw new ManagedCleanupException("managed_cleanup_absence_unconfirmed");
                        claim = store.RecordManagedCleanupConversationDeleted(claim);
                    }
                    finished = store.ConfirmManagedCleanup(claim);
                }
                if (finished)
                    confirmed++;
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                break;
            }
            catch (ManagedCleanupException exception)
            {
                failures.Add(exception.Code);
                if (exception.Code is "managed_cleanup_not_authorized" or "managed_cleanup_remote_unavailable"
                    or "managed_cleanup_run_limit")
                    break;
            }
            finally
            {
                if (!finished)
                    _ = store.ReleaseManagedCleanup(claim);
            }
        }

        return new ManagedCleanupSummary(claims.Count, deleted, confirmed, purged, recoveryRequests, failures);
    }

    private async Task<ManagedCleanupClaim> DeleteResponsesAsync(
        ManagedCleanupClaim claim, string conversationId, int deleteCount,
        CancellationToken cancellationToken, Action<int> setDeleteCount)
    {
        var ids = await ReadAllIdsAsync(
            (after, token) => remote.ListResponsesAsync(conversationId, after, token), cancellationToken);
        if (claim.LastResponseId is { } responseId)
            ids.Add(responseId);
        ids = ids.Distinct(StringComparer.Ordinal).ToList();
        var existing = ids.ToHashSet(StringComparer.Ordinal);
        var checkpointed = claim.DeletedResponseIds.ToHashSet(StringComparer.Ordinal);
        if (checkpointed.Overlaps(existing))
            throw new ManagedCleanupException("managed_cleanup_absence_changed");
        var attempted = new List<string>();
        foreach (var id in ids.Where(id => !checkpointed.Contains(id)))
        {
            if (!existing.Contains(id))
            {
                claim = store.RecordManagedCleanupResponseDeleted(claim, id);
                continue;
            }
            if (deleteCount >= MaximumDeletesPerRun)
                throw new ManagedCleanupException("managed_cleanup_run_limit");
            await remote.DeleteResponseAsync(id, cancellationToken);
            deleteCount++;
            setDeleteCount(deleteCount);
            attempted.Add(id);
        }
        if (attempted.Count > 0)
        {
            var remaining = await ReadAllIdsAsync(
                (after, token) => remote.ListResponsesAsync(conversationId, after, token), cancellationToken);
            foreach (var id in attempted)
            {
                if (remaining.Contains(id, StringComparer.Ordinal))
                    throw new ManagedCleanupException("managed_cleanup_absence_unconfirmed");
                claim = store.RecordManagedCleanupResponseDeleted(claim, id);
            }
        }
        return claim;
    }

    private async Task<ManagedCleanupClaim> DeleteItemsAsync(
        ManagedCleanupClaim claim, string conversationId, int deleteCount,
        CancellationToken cancellationToken, Action<int> setDeleteCount)
    {
        var ids = await ReadAllIdsAsync(
            (after, token) => remote.ListItemsAsync(conversationId, after, token), cancellationToken);
        ids.AddRange(claim.ResponseItemIds);
        ids = ids.Distinct(StringComparer.Ordinal).ToList();
        var existing = ids.ToHashSet(StringComparer.Ordinal);
        var checkpointed = claim.DeletedItemIds.ToHashSet(StringComparer.Ordinal);
        if (checkpointed.Overlaps(existing))
            throw new ManagedCleanupException("managed_cleanup_absence_changed");
        var attempted = new List<string>();
        foreach (var id in ids.Where(id => !checkpointed.Contains(id)))
        {
            if (!existing.Contains(id))
            {
                claim = store.RecordManagedCleanupItemDeleted(claim, id);
                continue;
            }
            if (deleteCount >= MaximumDeletesPerRun)
                throw new ManagedCleanupException("managed_cleanup_run_limit");
            await remote.DeleteItemAsync(conversationId, id, cancellationToken);
            deleteCount++;
            setDeleteCount(deleteCount);
            attempted.Add(id);
        }
        if (attempted.Count > 0)
        {
            var remaining = await ReadAllIdsAsync(
                (after, token) => remote.ListItemsAsync(conversationId, after, token), cancellationToken);
            foreach (var id in attempted)
            {
                if (remaining.Contains(id, StringComparer.Ordinal))
                    throw new ManagedCleanupException("managed_cleanup_absence_unconfirmed");
                claim = store.RecordManagedCleanupItemDeleted(claim, id);
            }
        }
        return claim;
    }

    private static async Task<List<string>> ReadAllIdsAsync(
        Func<string?, CancellationToken, Task<JsonObject>> readPage, CancellationToken cancellationToken)
    {
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? after = null;
        do
        {
            var page = await readPage(after, cancellationToken);
            if (page["data"] is not JsonArray data || data.Count > 100
                || page["has_more"] is not JsonValue moreValue || !moreValue.TryGetValue<bool>(out var more))
                throw new ManagedCleanupException("managed_cleanup_service_contract_invalid");
            string? last = null;
            foreach (var entry in data)
            {
                var id = entry is JsonObject item ? item["id"]?.GetValue<string>() : null;
                if (!IsIdentifier(id) || !seen.Add(id!))
                    throw new ManagedCleanupException("managed_cleanup_service_contract_invalid");
                ids.Add(id!);
                last = id;
            }
            if (!more)
                return ids;
            if (last is null || page["last_id"]?.GetValue<string>() != last || !cursors.Add(last))
                throw new ManagedCleanupException("managed_cleanup_service_contract_invalid");
            after = last;
        } while (true);
    }

    private static bool IsIdentifier(string? value) =>
        value is { Length: > 0 and <= 128 }
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
}

public static class ManagedCleanupCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 1)
            throw new ArgumentException("Usage: AirlineDemo.Deployment managed-cleanup <protected configuration JSON>");
        using var config = JsonDocument.Parse(await File.ReadAllTextAsync(args[0]));
        var root = config.RootElement;
        var endpoint = new Uri(root.GetProperty("projectEndpoint").GetString()!, UriKind.Absolute);
        var clientId = root.GetProperty("cleanupClientId").GetGuid();
        var server = root.GetProperty("server").GetString()!;
        var database = root.GetProperty("database").GetString()!;
        if (clientId == Guid.Empty || !Regex.IsMatch(server, "^[A-Za-z0-9.-]{1,253}$", RegexOptions.CultureInvariant)
            || !Regex.IsMatch(database, "^[A-Za-z0-9_-]{1,128}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Cleanup configuration contains an invalid identity or database target.");
        var credential = new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(clientId.ToString()));
        var token = await credential.GetTokenAsync(
            new TokenRequestContext(["https://database.windows.net/.default"]), CancellationToken.None);
        var connectionString = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder
        {
            DataSource = $"tcp:{server},1433", InitialCatalog = database,
            Encrypt = true, TrustServerCertificate = false, ConnectTimeout = 30,
        }.ConnectionString;
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString) { AccessToken = token.Token };
        await connection.OpenAsync();
        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<CaseStateDbContext>()
            .UseSqlServer(connection).Options;
        var store = new SqlManagedConversationStore(options);
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var cleanup = new ManagedConversationCleanup(store, new ManagedCleanupClient(endpoint, http, credential));
        var summary = await cleanup.RunAsync();
        Console.WriteLine($"Managed cleanup: claimed={summary.Claimed}, deleted={summary.Deleted}, " +
            $"confirmed={summary.Confirmed}, purged={summary.Purged}, " +
            $"recoveryRequests={summary.RecoveryRequests}, failed={summary.Failed}, " +
            $"failureCodes={string.Join(",", summary.FailureCodes.Distinct(StringComparer.Ordinal))}");
        return summary.Failed == 0 ? 0 : 1;
    }
}

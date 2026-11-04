using System.Security.Cryptography;
using System.Text;
using AirlineDemo.Api.Auth;
using AirlineDemo.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AirlineDemo.Api.Copilot;

public enum CopilotProvenance { CopilotClient, McpDirect, TrustedUi }

public enum CopilotOutcome { Succeeded, Denied, Failed, Cancelled }

/// <summary>One append-only ledger row; the field set is exactly the PRD audit contract.</summary>
public sealed record CopilotInvocationRecord(
    Guid InvocationId, string Phase, DateTimeOffset At, string Operation, string Provenance, string? CallerRef, string? SessionRef,
    string Role, string? AuthorizedCaseRef, string Outcome, int? HttpStatus, string? ResultCode);

public sealed record CopilotAdmission(CopilotInvocationRecord Record)
{
    public Guid InvocationId => Record.InvocationId;
}

public sealed class CopilotAuditUnavailableException() : Exception("Copilot invocation audit is unavailable.");

public interface ICopilotInvocationStore
{
    Task AppendAsync(CopilotInvocationRecord record, CancellationToken cancellationToken);
    Task<IReadOnlyList<CopilotInvocationRecord>> ReadCaseAsync(string authorizedCaseRef, CancellationToken cancellationToken);
}

public interface ICopilotInvocationLedger
{
    Task<CopilotAdmission> Admit(string operation, CopilotProvenance provenance, DemoSession? session, string? authorizedCaseRef,
        CancellationToken cancellationToken = default);
    Task Outcome(CopilotAdmission admission, int httpStatus, string resultCode, CopilotOutcome outcome,
        CancellationToken cancellationToken = default);
    /// <summary>Builds the outcome row for a store that appends it inside its own transaction (the copilot release).</summary>
    CopilotInvocationRecord OutcomeRecord(CopilotAdmission admission, int httpStatus, string resultCode, CopilotOutcome outcome);
    /// <summary>The keyed, nonreversible reference for a user id, as recorded in the ledger.</summary>
    string CallerRef(string userId);
    /// <summary>The keyed, nonreversible reference for an exact demo session id, as recorded in the ledger.</summary>
    string SessionRef(string sessionId);
}

public sealed class CopilotInvocationLedger(ICopilotInvocationStore store, TimeProvider time, IConfiguration configuration,
    ILogger<CopilotInvocationLedger> logger) : ICopilotInvocationLedger
{
    public static readonly IReadOnlySet<string> Operations = new HashSet<string>(StringComparer.Ordinal)
    {
        "list_my_cases", "get_case_summary", "get_open_points", "read_source", "advance_stage", "question", "approve", "decline", "cancel",
        "unknown_tool",
    };
    public static readonly IReadOnlySet<string> ResultCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        "ok", "confirmation_required", "cancelled", "invalid_request", "unsupported_media_type", "unsupported_contract_version",
        "unsupported_protocol_version", "unknown_tool", "unauthenticated", "origin_forbidden", "host_forbidden", "anti_forgery_failed",
        "forbidden", "case_not_found", "case_selection_required", "selected_case_mismatch", "release_note_required", "invalid_release_note",
        "confirmation_not_found", "confirmation_mismatch", "confirmation_declined", "confirmation_cancelled", "confirmation_expired",
        "confirmation_state_conflict", "stale_revision", "already_released", "release_blocked", "replay_mismatch", "source_read_limit",
        "source_record_limit", "source_read_timeout", "source_case_mismatch", "record_not_found", "source_unavailable",
        "source_payload_limit", "source_read_failed", "copilot_point_limit", "copilot_context_invalid", "invalid_question",
        "copilot_input_not_approved", "model_manifest_refused", "model_unavailable", "model_response_invalid", "copilot_tool_limit",
        "copilot_model_limit", "copilot_retry_limit", "copilot_input_token_limit", "copilot_output_token_limit", "copilot_timeout",
        "citation_invalid", "audit_unavailable", "release_unavailable", "conversation_not_found", "conversation_busy",
        "conversation_context_mismatch", "conversation_expired", "conversation_turn_limit", "conversation_unavailable",
        "conversation_history_invalid", "conversation_store_unavailable", "managed_agent_unconfigured", "managed_operation_ambiguous",
        "managed_content_filtered", "managed_refusal",
    };

    private readonly byte[] _key = configuration["Copilot:AuditKey"] is { Length: > 0 } configured
        ? Convert.FromBase64String(configured)
        : RandomNumberGenerator.GetBytes(32);

    public async Task<CopilotAdmission> Admit(string operation, CopilotProvenance provenance, DemoSession? session, string? authorizedCaseRef,
        CancellationToken cancellationToken = default)
    {
        var role = session is null ? "anonymous" : CopilotProjections.Roles.Contains(session.User.Role, StringComparer.Ordinal) ? session.User.Role : "unknown";
        var record = new CopilotInvocationRecord(Guid.NewGuid(), "admit", time.GetUtcNow(),
            Operations.Contains(operation) ? operation : "rejected-operation", Provenance(provenance),
            session is null ? null : Ref("caller", session.User.Id), session is null ? null : Ref("session", session.Id),
            role, authorizedCaseRef, "admitted", null, null);
        await Append(record, "Copilot invocation admission could not be recorded; the invocation was stopped before dispatch.", cancellationToken);
        return new CopilotAdmission(record);
    }

    public Task Outcome(CopilotAdmission admission, int httpStatus, string resultCode, CopilotOutcome outcome,
        CancellationToken cancellationToken = default) =>
        Append(OutcomeRecord(admission, httpStatus, resultCode, outcome),
            "Copilot invocation outcome could not be recorded after admission; further dispatch was stopped.", cancellationToken);

    public CopilotInvocationRecord OutcomeRecord(CopilotAdmission admission, int httpStatus, string resultCode, CopilotOutcome outcome)
    {
        if (!ResultCodes.Contains(resultCode)) throw new ArgumentOutOfRangeException(nameof(resultCode));
        return admission.Record with
        {
            Phase = "outcome", At = time.GetUtcNow(), Outcome = outcome.ToString().ToLowerInvariant(), HttpStatus = httpStatus, ResultCode = resultCode,
        };
    }

    private async Task Append(CopilotInvocationRecord record, string diagnostic, CancellationToken cancellationToken)
    {
        try { await store.AppendAsync(record, cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
#pragma warning disable CA2254 // Each diagnostic is one of two fixed messages without identifiers.
            logger.LogError(diagnostic);
#pragma warning restore CA2254
            throw new CopilotAuditUnavailableException();
        }
    }

    private static string Provenance(CopilotProvenance provenance) => provenance switch
    {
        CopilotProvenance.CopilotClient => "copilot-client",
        CopilotProvenance.McpDirect => "mcp-direct",
        CopilotProvenance.TrustedUi => "trusted-ui",
        _ => throw new ArgumentOutOfRangeException(nameof(provenance)),
    };

    public string CallerRef(string userId) => Ref("caller", userId);

    public string SessionRef(string sessionId) => Ref("session", sessionId);

    private string Ref(string purpose, string value) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(purpose + "\0" + value)));
}

public sealed class InMemoryCopilotInvocationStore : ICopilotInvocationStore
{
    private readonly Lock _gate = new();
    private readonly List<CopilotInvocationRecord> _records = [];

    public IReadOnlyList<CopilotInvocationRecord> Records { get { lock (_gate) return [.. _records]; } }

    public Task AppendAsync(CopilotInvocationRecord record, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            // Same append-only key as the SQL table: one row per (invocation, phase).
            if (_records.Any(row => row.InvocationId == record.InvocationId && row.Phase == record.Phase))
                throw new InvalidOperationException("Duplicate invocation phase.");
            _records.Add(record);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CopilotInvocationRecord>> ReadCaseAsync(string authorizedCaseRef, CancellationToken cancellationToken)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<CopilotInvocationRecord>>([.. _records.Where(record => record.AuthorizedCaseRef == authorizedCaseRef)]);
    }
}

public sealed class CopilotInvocationRow
{
    public Guid InvocationId { get; set; }
    public string Phase { get; set; } = string.Empty;
    public long AtTicks { get; set; }
    public string Operation { get; set; } = string.Empty;
    public string Provenance { get; set; } = string.Empty;
    public string? CallerRef { get; set; }
    public string? SessionRef { get; set; }
    public string Role { get; set; } = string.Empty;
    public string? AuthorizedCaseRef { get; set; }
    public string Outcome { get; set; } = string.Empty;
    public int? HttpStatus { get; set; }
    public string? ResultCode { get; set; }
}

public sealed class SqlCopilotInvocationStore(DbContextOptions<CaseStateDbContext> options) : ICopilotInvocationStore
{
    public async Task AppendAsync(CopilotInvocationRecord record, CancellationToken cancellationToken)
    {
        await using var db = new CaseStateDbContext(options);
        db.CopilotInvocations.Add(ToRow(record));
        await db.SaveChangesAsync(cancellationToken);
    }

    internal static CopilotInvocationRow ToRow(CopilotInvocationRecord record) => new()
    {
        InvocationId = record.InvocationId, Phase = record.Phase, AtTicks = record.At.UtcTicks, Operation = record.Operation,
        Provenance = record.Provenance, CallerRef = record.CallerRef, SessionRef = record.SessionRef, Role = record.Role,
        AuthorizedCaseRef = record.AuthorizedCaseRef, Outcome = record.Outcome, HttpStatus = record.HttpStatus, ResultCode = record.ResultCode,
    };

    public async Task<IReadOnlyList<CopilotInvocationRecord>> ReadCaseAsync(string authorizedCaseRef, CancellationToken cancellationToken)
    {
        await using var db = new CaseStateDbContext(options);
        var rows = await db.CopilotInvocations.AsNoTracking().Where(row => row.AuthorizedCaseRef == authorizedCaseRef)
            .OrderBy(row => row.AtTicks).ToListAsync(cancellationToken);
        return [.. rows.Select(row => new CopilotInvocationRecord(row.InvocationId, row.Phase, new DateTimeOffset(row.AtTicks, TimeSpan.Zero),
            row.Operation, row.Provenance, row.CallerRef, row.SessionRef, row.Role, row.AuthorizedCaseRef, row.Outcome, row.HttpStatus, row.ResultCode))];
    }
}

public static class CopilotInvocationLedgerRegistration
{
    public static IServiceCollection AddCopilotInvocationLedger(this IServiceCollection services, IConfiguration configuration)
    {
        if (string.Equals(configuration["Persistence:CaseStateProvider"], "Sql", StringComparison.OrdinalIgnoreCase))
        {
            var connectionString = configuration.GetConnectionString(CaseStateStoreRegistration.ConnectionStringName);
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new InvalidOperationException("ConnectionStrings:CaseState is required when Persistence:CaseStateProvider is Sql.");
            CaseStateStoreRegistration.ValidateManagedIdentityConnectionString(connectionString);
            var options = new DbContextOptionsBuilder<CaseStateDbContext>().UseSqlServer(connectionString).Options;
            services.AddSingleton<ICopilotInvocationStore>(new SqlCopilotInvocationStore(options));
            services.AddSingleton<ICopilotInvocationDetailStore>(new SqlCopilotInvocationDetailStore(options));
        }
        else
        {
            services.AddSingleton<ICopilotInvocationStore, InMemoryCopilotInvocationStore>();
            services.AddSingleton<ICopilotInvocationDetailStore, InMemoryCopilotInvocationDetailStore>();
        }
        services.AddSingleton<ICopilotInvocationLedger, CopilotInvocationLedger>();
        return services;
    }
}

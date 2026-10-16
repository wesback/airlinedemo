using Microsoft.EntityFrameworkCore;

namespace AirlineDemo.Api.Persistence;

public sealed class CaseRow
{
    public string CaseId { get; set; } = string.Empty;
    public string Owner { get; set; } = string.Empty;
    public long Revision { get; set; }
}

public sealed class CaseDocumentRow
{
    public long Id { get; set; }
    public string CaseId { get; set; } = string.Empty;
    public int Version { get; set; }
    public string Content { get; set; } = string.Empty;
    public string ContentDigest { get; set; } = string.Empty;
    public string BasisDigest { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class CaseDecisionRow
{
    public long Id { get; set; }
    public string CaseId { get; set; } = string.Empty;
    public string ReceiptId { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
}

public sealed class CaseEventRow
{
    public long Sequence { get; set; }
    public string CaseId { get; set; } = string.Empty;
    public long Revision { get; set; }
    public string Actor { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public DateTimeOffset At { get; set; }
}

public sealed class WorkItemRow
{
    public string CaseId { get; set; } = string.Empty;
    public string ItemId { get; set; } = string.Empty;
    public string CapabilityId { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string Owner { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class InvestigationTraceRow
{
    public byte[] KeyHash { get; set; } = [];
    public string CaseId { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public string InvestigationId { get; set; } = string.Empty;
    public int BasisVersion { get; set; }
    public string ModelDeploymentName { get; set; } = string.Empty;
    public string ApiVersion { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string ContentFilterSetting { get; set; } = string.Empty;
    public string Decision { get; set; } = string.Empty;
    public string ActorToken { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
}

public sealed class InvestigationTraceStepRow
{
    public byte[] KeyHash { get; set; } = [];
    public int Sequence { get; set; }
    public string ToolName { get; set; } = string.Empty;
    public string DocumentId { get; set; } = string.Empty;
    public int PageNumber { get; set; }
    public string CitationCheckOutcome { get; set; } = string.Empty;
    public string CorrectionCode { get; set; } = string.Empty;
    public string PolicyHandover { get; set; } = string.Empty;
}

/// <summary>
/// EF Core model for the provisioned Azure SQL case-state schema. Schema changes are applied only
/// by the migration identity; the app identity holds data-plane grants only (see identity-and-telemetry.tf).
/// </summary>
public sealed class CaseStateDbContext(DbContextOptions<CaseStateDbContext> options) : DbContext(options)
{
    public DbSet<CaseRow> Cases => Set<CaseRow>();
    public DbSet<CaseDocumentRow> CaseDocuments => Set<CaseDocumentRow>();
    public DbSet<CaseDecisionRow> CaseDecisions => Set<CaseDecisionRow>();
    public DbSet<CaseEventRow> CaseEvents => Set<CaseEventRow>();
    public DbSet<WorkItemRow> WorkItems => Set<WorkItemRow>();
    public DbSet<InvestigationTraceRow> InvestigationTraces => Set<InvestigationTraceRow>();
    public DbSet<InvestigationTraceStepRow> InvestigationTraceSteps => Set<InvestigationTraceStepRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<InvestigationTraceRow>(e =>
        {
            e.ToTable("investigation_traces", "dbo");
            e.HasKey(x => x.KeyHash);
            e.HasIndex(x => new { x.CaseId, x.RunId, x.InvestigationId })
                .IsUnique()
                .HasDatabaseName("UX_investigation_traces_case_run_investigation");
            e.Property(x => x.KeyHash).HasMaxLength(32).IsFixedLength();
            e.Property(x => x.CaseId).HasMaxLength(128);
            e.Property(x => x.RunId).HasMaxLength(128);
            e.Property(x => x.InvestigationId).HasMaxLength(128);
            e.Property(x => x.ModelDeploymentName).HasMaxLength(256).IsRequired();
            e.Property(x => x.ApiVersion).HasMaxLength(64).IsRequired();
            e.Property(x => x.Region).HasMaxLength(64).IsRequired();
            e.Property(x => x.ContentFilterSetting).HasMaxLength(128).IsRequired();
            e.Property(x => x.Decision).HasMaxLength(128).IsRequired();
            e.Property(x => x.ActorToken).HasMaxLength(256).IsRequired();
            e.Property(x => x.Outcome).HasMaxLength(128).IsRequired();
        });

        modelBuilder.Entity<InvestigationTraceStepRow>(e =>
        {
            e.ToTable("investigation_trace_steps", "dbo");
            e.HasKey(x => new { x.KeyHash, x.Sequence });
            e.Property(x => x.KeyHash).HasMaxLength(32).IsFixedLength();
            e.Property(x => x.ToolName).HasMaxLength(128).IsRequired();
            e.Property(x => x.DocumentId).HasMaxLength(256).IsRequired();
            e.Property(x => x.CitationCheckOutcome).HasMaxLength(64).IsRequired();
            e.Property(x => x.CorrectionCode).HasMaxLength(128).IsRequired();
            e.Property(x => x.PolicyHandover).HasMaxLength(128).IsRequired();
        });

        modelBuilder.Entity<CaseRow>(e =>
        {
            e.ToTable("cases", "dbo");
            e.HasKey(x => x.CaseId);
            e.Property(x => x.CaseId).HasMaxLength(128);
            e.Property(x => x.Owner).HasMaxLength(256).IsRequired();
        });

        modelBuilder.Entity<CaseDocumentRow>(e =>
        {
            e.ToTable("case_documents", "dbo");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityColumn();
            e.Property(x => x.CaseId).HasMaxLength(128).IsRequired();
            e.Property(x => x.Content).IsRequired();
            e.Property(x => x.ContentDigest).HasMaxLength(128).IsRequired();
            e.Property(x => x.BasisDigest).HasMaxLength(128).IsRequired();
        });

        modelBuilder.Entity<CaseDecisionRow>(e =>
        {
            e.ToTable("case_decisions", "dbo");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityColumn();
            e.Property(x => x.CaseId).HasMaxLength(128).IsRequired();
            e.Property(x => x.ReceiptId).HasMaxLength(128).IsRequired();
            e.Property(x => x.Fingerprint).HasMaxLength(128).IsRequired();
        });

        modelBuilder.Entity<WorkItemRow>(e =>
        {
            e.ToTable("case_work_items", "dbo");
            e.HasKey(x => new { x.CaseId, x.ItemId });
            e.Property(x => x.CaseId).HasMaxLength(128);
            e.Property(x => x.ItemId).HasMaxLength(128);
            e.Property(x => x.CapabilityId).HasMaxLength(128).IsRequired();
            e.Property(x => x.State).HasMaxLength(64).IsRequired();
            e.Property(x => x.Owner).HasMaxLength(256).IsRequired();
        });

        modelBuilder.Entity<CaseEventRow>(e =>
        {
            e.ToTable("case_events", "audit");
            e.HasKey(x => x.Sequence);
            e.Property(x => x.Sequence).UseIdentityColumn();
            e.Property(x => x.CaseId).HasMaxLength(128).IsRequired();
            e.Property(x => x.Actor).HasMaxLength(256).IsRequired();
            e.Property(x => x.Action).HasMaxLength(128).IsRequired();
            e.Property(x => x.Detail).IsRequired();
        });
    }
}

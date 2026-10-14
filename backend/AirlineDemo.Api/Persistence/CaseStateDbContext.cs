using Microsoft.EntityFrameworkCore;

namespace AirlineDemo.Api.Persistence;

public sealed class CaseStateDbContext(DbContextOptions<CaseStateDbContext> options) : DbContext(options)
{
    public DbSet<CaseRow> Cases => Set<CaseRow>();
    public DbSet<CaseDocumentRow> CaseDocuments => Set<CaseDocumentRow>();
    public DbSet<CaseDecisionRow> CaseDecisions => Set<CaseDecisionRow>();
    public DbSet<CaseEventRow> CaseEvents => Set<CaseEventRow>();

    // SQL Server uses the dbo/audit schemas; the SQLite test provider has no schemas.
    private string? Schema(string schema) =>
        Database.ProviderName == "Microsoft.EntityFrameworkCore.SqlServer" ? schema : null;

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<CaseRow>(e =>
        {
            e.ToTable("cases", Schema("dbo"));
            e.HasKey(x => x.CaseId);
            e.Property(x => x.CaseId).HasMaxLength(128);
            e.Property(x => x.Owner).HasMaxLength(256);
        });
        model.Entity<CaseDocumentRow>(e =>
        {
            e.ToTable("case_documents", Schema("dbo"));
            e.HasKey(x => new { x.CaseId, x.Version });
            e.Property(x => x.CaseId).HasMaxLength(128);
            e.Property(x => x.ContentDigest).HasMaxLength(128);
            e.Property(x => x.BasisDigest).HasMaxLength(128);
        });
        model.Entity<CaseDecisionRow>(e =>
        {
            e.ToTable("case_decisions", Schema("dbo"));
            e.HasKey(x => new { x.CaseId, x.ReceiptId });
            e.Property(x => x.CaseId).HasMaxLength(128);
            e.Property(x => x.ReceiptId).HasMaxLength(256);
            e.Property(x => x.Fingerprint).HasMaxLength(128);
        });
        model.Entity<CaseEventRow>(e =>
        {
            e.ToTable("case_events", Schema("audit"));
            e.HasKey(x => new { x.CaseId, x.Sequence });
            e.Property(x => x.CaseId).HasMaxLength(128);
            e.Property(x => x.Actor).HasMaxLength(256);
            e.Property(x => x.Action).HasMaxLength(128);
        });
    }
}

public sealed class CaseRow
{
    public string CaseId { get; set; } = string.Empty;
    public string Owner { get; set; } = string.Empty;
    public long Revision { get; set; }
}

public sealed class CaseDocumentRow
{
    public string CaseId { get; set; } = string.Empty;
    public int Version { get; set; }
    public string Content { get; set; } = string.Empty;
    public string ContentDigest { get; set; } = string.Empty;
    public string BasisDigest { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class CaseDecisionRow
{
    public string CaseId { get; set; } = string.Empty;
    public string ReceiptId { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public long Revision { get; set; }
}

public sealed class CaseEventRow
{
    public string CaseId { get; set; } = string.Empty;
    public long Sequence { get; set; }
    public long Revision { get; set; }
    public string Actor { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public DateTimeOffset At { get; set; }
}

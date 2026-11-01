using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Nocturne.Core.Contracts.Jobs;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>A row of <see cref="IJobLedger"/>, which states what the table holds.</summary>
[Table("job_ledger")]
public class JobLedgerEntity
{
    [Key]
    public Guid Id { get; set; }

    [Column("job")]
    public string Job { get; set; } = string.Empty;

    /// <summary>Null for a platform job's one row.</summary>
    [Column("tenant_id")]
    public Guid? TenantId { get; set; }

    [Column("last_started_at")]
    public DateTime? LastStartedAt { get; set; }

    [Column("last_completed_at")]
    public DateTime? LastCompletedAt { get; set; }

    /// <summary>Null once the job has finished for the row: it never comes due again.</summary>
    [Column("next_due_at")]
    public DateTime? NextDueAt { get; set; }

    [Column("lease_holder")]
    public string? LeaseHolder { get; set; }

    [Column("lease_until")]
    public DateTime? LeaseUntil { get; set; }

    /// <summary>Failed runs since the last success.</summary>
    [Column("attempts")]
    public int Attempts { get; set; }

    /// <summary>The last failure's exception type and message.</summary>
    [Column("last_error")]
    public string? LastError { get; set; }

    /// <summary>A serialised <see cref="JobCursor"/>.</summary>
    [Column("cursor", TypeName = "jsonb")]
    public string? Cursor { get; set; }
}

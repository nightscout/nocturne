using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nocturne.Infrastructure.Data.Entities;

/// <summary>
/// A job a migration leaves for the API to run once for a tenant after startup, because SQL cannot
/// do it. The API deletes the row once it has run the job.
/// </summary>
/// <remarks>
/// Not tenant-scoped (no RLS): the startup sweep reads it across tenants, and a row holds a job name
/// only, no health data.
/// </remarks>
[Table("pending_tenant_jobs")]
public class PendingTenantJobEntity
{
    /// <summary>The tenant the job runs for.</summary>
    [Column("tenant_id")]
    public Guid TenantId { get; set; }

    /// <summary>The job, one of <see cref="PendingTenantJobs"/>.</summary>
    [Column("job")]
    [MaxLength(64)]
    public string Job { get; set; } = string.Empty;

    /// <summary>When the migration asked for it.</summary>
    [Column("requested_at")]
    public DateTime RequestedAt { get; set; }
}

/// <summary>The jobs a <see cref="PendingTenantJobEntity"/> names.</summary>
public static class PendingTenantJobs
{
    /// <summary>
    /// The tenant's profile versions moved without a write: re-resolve its schedule-derived temp
    /// basal rates and drop its cached reads.
    /// </summary>
    public const string ProfileVersionsMoved = "profile-versions-moved";
}

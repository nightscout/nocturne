using System.ComponentModel.DataAnnotations;

namespace Nocturne.API.Models.Responses;

public class AuditConfigDto
{
    public bool ReadAuditEnabled { get; set; }

    /// <summary>
    /// Days of read-access audit to keep, or null for the platform default. Bounded below
    /// because a zero or negative window puts the purge cutoff at or after now, which deletes
    /// the access record for reads that just happened.
    /// </summary>
    [Range(MinRetentionDays, MaxRetentionDays)]
    public int? ReadAuditRetentionDays { get; set; }

    /// <summary>
    /// Days of mutation audit to keep, or null for the platform default. Additionally floored
    /// at the tenant's effective soft-delete window by
    /// <c>AuditController.UpdateAuditConfig</c>.
    /// </summary>
    [Range(MinRetentionDays, MaxRetentionDays)]
    public int? MutationAuditRetentionDays { get; set; }

    private const int MinRetentionDays = 1;
    private const int MaxRetentionDays = 3650;
}

namespace Nocturne.API.Models.Responses;

public class AuditConfigDto
{
    public bool ReadAuditEnabled { get; set; }
    public int? ReadAuditRetentionDays { get; set; }
    public int? MutationAuditRetentionDays { get; set; }
}

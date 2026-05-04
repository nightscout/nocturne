namespace Nocturne.API.Models.Responses;

public class MutationAuditDto
{
    public Guid Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public string EntityType { get; set; } = null!;
    public Guid EntityId { get; set; }
    public string Action { get; set; } = null!;
    public string? Changes { get; set; }
    public Guid? SubjectId { get; set; }
    public string? SubjectName { get; set; }
    public string? AuthType { get; set; }
    public string? IpAddress { get; set; }
    public string? Endpoint { get; set; }
    public string? Reason { get; set; }
}

namespace Nocturne.API.Models.Responses;

public class ReadAccessAuditDto
{
    public Guid Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public string Endpoint { get; set; } = null!;
    public string? EntityType { get; set; }
    public int? RecordCount { get; set; }
    public int StatusCode { get; set; }
    public string? QueryParameters { get; set; }
    public Guid? SubjectId { get; set; }
    public string? SubjectName { get; set; }
    public string? AuthType { get; set; }
    public string? IpAddress { get; set; }
    public string? ApiSecretHashPrefix { get; set; }
}

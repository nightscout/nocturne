using Nocturne.Core.Contracts.Audit;

namespace Nocturne.API.Services.Audit;

/// <summary>
/// Mutable scoped implementation of IAuditContext.
/// Populated by AuditContextMiddleware for HTTP requests
/// or manually by background services.
/// </summary>
public class AuditContext : IAuditContext
{
    public Guid? SubjectId { get; set; }
    public string? SubjectName { get; set; }
    public string? AuthType { get; set; }
    public string? IpAddress { get; set; }
    public Guid? TokenId { get; set; }
    public string? CorrelationId { get; set; }
    public string? Endpoint { get; set; }
}

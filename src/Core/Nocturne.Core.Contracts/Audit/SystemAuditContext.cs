namespace Nocturne.Core.Contracts.Audit;

/// <summary>
/// Audit context for background service mutations (connectors, demo data generator).
/// These services have no HTTP request, so <c>AuditContextMiddleware</c> never runs.
/// Instead, they create a SystemAuditContext and attach it to the DbContext directly.
/// </summary>
public sealed class SystemAuditContext : IAuditContext
{
    public Guid? SubjectId => null;
    public string? SubjectName => null;
    public string? AuthType { get; init; }
    public string? IpAddress => null;
    public Guid? TokenId => null;
    public string? CorrelationId { get; init; }
    public string? Endpoint { get; init; }

    /// <summary>
    /// Creates a system audit context for a background service endpoint.
    /// </summary>
    /// <param name="endpoint">Descriptive identifier (e.g. "service:demo-generator", "connector:dexcom")</param>
    public static SystemAuditContext ForService(string endpoint) => new()
    {
        AuthType = "system",
        Endpoint = endpoint,
        CorrelationId = Guid.CreateVersion7().ToString()
    };
}

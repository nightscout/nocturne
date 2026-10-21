using Nocturne.Core.Contracts.Audit;

namespace Nocturne.API.Services.Audit;

/// <summary>
/// Mutable scoped implementation of IAuditContext.
/// Populated by AuditContextMiddleware for HTTP requests
/// or manually by background services.
/// </summary>
public class AuditContext : IAuditContext, ISystemAttributable
{
    /// <summary>
    /// The <see cref="Microsoft.AspNetCore.Http.HttpContext.Items"/> key the request's populated
    /// context is kept under, for the SignalR invocation scopes that serve its connection.
    /// </summary>
    public const string HttpItemKey = "AuditContext";

    public Guid? SubjectId { get; set; }
    public string? SubjectName { get; set; }
    public string? AuthType { get; set; }
    public string? IpAddress { get; set; }
    public Guid? TokenId { get; set; }
    public string? TraceId { get; set; }
    public string? Endpoint { get; set; }
    public bool IsSystem { get; set; }

    /// <summary>Takes every actor field of <paramref name="source"/>.</summary>
    public void CopyFrom(IAuditContext source)
    {
        SubjectId = source.SubjectId;
        SubjectName = source.SubjectName;
        AuthType = source.AuthType;
        IpAddress = source.IpAddress;
        TokenId = source.TokenId;
        TraceId = source.TraceId;
        Endpoint = source.Endpoint;
        IsSystem = source.IsSystem;
    }

    /// <inheritdoc />
    public void AttributeToSystem(string endpoint)
    {
        var system = SystemAuditContext.ForService(endpoint);
        SubjectId = null;
        SubjectName = null;
        TokenId = null;
        IpAddress = null;
        AuthType = system.AuthType;
        Endpoint = system.Endpoint;
        TraceId = system.TraceId;
        IsSystem = true;
    }
}

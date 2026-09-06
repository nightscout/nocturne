using Nocturne.Core.Contracts.Audit;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Services.Audit;

/// <summary>
/// Mutates a scoped <see cref="IAuditContext"/> into system-attribution mode for
/// the duration of the scope. Actor fields (subject_id, subject_name, auth_type,
/// token_id, ip_address) are nulled; trace fields (correlation_id, endpoint) are
/// preserved so the resulting audit rows remain tied to the originating request.
/// Restores the original field values on dispose. Use around connector-pipeline
/// sweep operations (DeleteBy*, decomposer cleanup) that should not appear in
/// the discriminator query as user-attributed.
/// </summary>
public sealed class SystemAuditScope : IDisposable
{
    private readonly AuditContext _target;
    private readonly Guid? _subjectId;
    private readonly string? _subjectName;
    private readonly string? _authType;
    private readonly Guid? _tokenId;
    private readonly string? _ipAddress;
    private readonly bool _isSystem;

    private SystemAuditScope(AuditContext target)
    {
        _target = target;
        _subjectId = target.SubjectId;
        _subjectName = target.SubjectName;
        _authType = target.AuthType;
        _tokenId = target.TokenId;
        _ipAddress = target.IpAddress;
        _isSystem = target.IsSystem;

        target.SubjectId = null;
        target.SubjectName = null;
        target.AuthType = null;
        target.TokenId = null;
        target.IpAddress = null;
        target.IsSystem = true;
    }

    /// <summary>
    /// Push a system-attribution scope onto <paramref name="ambient"/>.
    /// Returns a no-op disposable if <paramref name="ambient"/> is not a mutable
    /// <see cref="AuditContext"/> (e.g. a test double exposing only the interface),
    /// so callers can use the scope uniformly without null-checking.
    /// </summary>
    public static IDisposable Push(IAuditContext ambient)
        => ambient is AuditContext mutable
            ? new SystemAuditScope(mutable)
            : NoOpScope.Instance;

    /// <summary>
    /// System-attributes every write a DI scope makes, on both context paths: the scope's own
    /// <see cref="NocturneDbContext"/> carries its own <see cref="IAuditContext"/> property, which
    /// the interceptor prefers over the ambient one, while the contexts
    /// <c>ITenantDbContextFactory</c> creates are stamped from the scoped ambient context. Covering
    /// only one leaves the other attributing the scope's writes to whoever started it — and the
    /// interceptor derives <c>DeletedByUser</c> from that, which blocks a later resync from
    /// re-creating rows a sync soft-deleted.
    /// </summary>
    /// <param name="scopeServices">The child scope's service provider.</param>
    /// <param name="endpoint">Descriptive identifier, e.g. <c>"connector:dexcom"</c>.</param>
    public static IDisposable PushForScope(IServiceProvider scopeServices, string endpoint)
    {
        scopeServices.GetRequiredService<NocturneDbContext>().AuditContext =
            SystemAuditContext.ForService(endpoint);
        return Push(scopeServices.GetRequiredService<IAuditContext>());
    }

    public void Dispose()
    {
        _target.SubjectId = _subjectId;
        _target.SubjectName = _subjectName;
        _target.AuthType = _authType;
        _target.TokenId = _tokenId;
        _target.IpAddress = _ipAddress;
        _target.IsSystem = _isSystem;
    }

    private sealed class NoOpScope : IDisposable
    {
        public static readonly NoOpScope Instance = new();
        public void Dispose() { }
    }
}

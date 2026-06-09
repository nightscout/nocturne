using Nocturne.Core.Contracts.Audit;

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

    private SystemAuditScope(AuditContext target)
    {
        _target = target;
        _subjectId = target.SubjectId;
        _subjectName = target.SubjectName;
        _authType = target.AuthType;
        _tokenId = target.TokenId;
        _ipAddress = target.IpAddress;

        target.SubjectId = null;
        target.SubjectName = null;
        target.AuthType = null;
        target.TokenId = null;
        target.IpAddress = null;
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

    public void Dispose()
    {
        _target.SubjectId = _subjectId;
        _target.SubjectName = _subjectName;
        _target.AuthType = _authType;
        _target.TokenId = _tokenId;
        _target.IpAddress = _ipAddress;
    }

    private sealed class NoOpScope : IDisposable
    {
        public static readonly NoOpScope Instance = new();
        public void Dispose() { }
    }
}

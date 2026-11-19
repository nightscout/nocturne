using Nocturne.Core.Contracts.Audit;

namespace Nocturne.API.Services.Audit;

/// <summary>
/// Mutates a scoped <see cref="IAuditContext"/> into system-attribution mode for
/// the duration of the scope. Actor fields (actor_subject_id, actor_name, auth_type,
/// token_id, credential fingerprint, ip_address, user agent) are nulled; trace fields (correlation_id, endpoint) are
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
    private readonly string? _credentialFingerprint;
    private readonly string? _userAgent;
    private readonly bool _isSystem;
    private readonly Guid? _keptWriter;

    private SystemAuditScope(AuditContext target, bool keepWriter)
    {
        _keptWriter = target.KeptWriterGrantId;
        target.KeptWriterGrantId = keepWriter ? target.WriterGrantId : null;

        _target = target;
        _subjectId = target.SubjectId;
        _subjectName = target.SubjectName;
        _authType = target.AuthType;
        _tokenId = target.TokenId;
        _ipAddress = target.IpAddress;
        _credentialFingerprint = target.CredentialFingerprint;
        _userAgent = target.UserAgent;
        _isSystem = target.IsSystem;

        target.SubjectId = null;
        target.SubjectName = null;
        target.AuthType = null;
        target.TokenId = null;
        target.IpAddress = null;
        target.CredentialFingerprint = null;
        target.UserAgent = null;
        target.IsSystem = true;
    }

    /// <summary>
    /// Push a system-attribution scope onto <paramref name="ambient"/>.
    /// Returns a no-op disposable if <paramref name="ambient"/> is not a mutable
    /// <see cref="AuditContext"/> (e.g. a test double exposing only the interface),
    /// so callers can use the scope uniformly without null-checking.
    /// </summary>
    /// <param name="ambient">The scope's audit context.</param>
    /// <param name="keepWriter">
    /// Whether rows inserted inside the scope are still stamped with the caller's grant
    /// (<see cref="IAuditContext.WriterGrantId"/>). True for an uploader's own batch, which is
    /// system-attributed only to stay out of the audit log; false for work the caller started and
    /// did not write, such as a connector sync.
    /// </param>
    public static IDisposable Push(IAuditContext ambient, bool keepWriter = false)
        => ambient is AuditContext mutable
            ? new SystemAuditScope(mutable, keepWriter)
            : NoOpScope.Instance;

    public void Dispose()
    {
        _target.SubjectId = _subjectId;
        _target.SubjectName = _subjectName;
        _target.AuthType = _authType;
        _target.TokenId = _tokenId;
        _target.IpAddress = _ipAddress;
        _target.CredentialFingerprint = _credentialFingerprint;
        _target.UserAgent = _userAgent;
        _target.IsSystem = _isSystem;
        _target.KeptWriterGrantId = _keptWriter;
    }

    private sealed class NoOpScope : IDisposable
    {
        public static readonly NoOpScope Instance = new();
        public void Dispose() { }
    }
}

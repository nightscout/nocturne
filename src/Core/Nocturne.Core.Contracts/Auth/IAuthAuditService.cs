using Nocturne.Core.Models.Authorization;

namespace Nocturne.Core.Contracts.Auth;

/// <summary>
/// Who performed an audited action, when that is not the subject the action was performed on.
/// </summary>
/// <param name="SubjectId">The acting subject, when the caller has one of its own.</param>
/// <param name="Credential">
/// Identifies the acting credential for callers with no subject, e.g.
/// <c>InstanceKey:&lt;fingerprint&gt;</c>. Never carries key material; see
/// <see cref="AuthContext.CredentialFingerprint"/>.
/// </param>
public sealed record AuthAuditActor(Guid? SubjectId, string? Credential)
{
    private const string UnknownCredential = "unknown";

    /// <summary>
    /// Describes the caller behind <paramref name="auth"/> as an actor. Returns an actor in every
    /// case, including an unauthenticated request, so that a caller acting on someone else's
    /// behalf is never mistaken for that someone acting on their own.
    /// </summary>
    public static AuthAuditActor From(AuthContext? auth) => auth switch
    {
        null => new AuthAuditActor(null, UnknownCredential),
        { SubjectId: not null } => new AuthAuditActor(auth.SubjectId, null),
        { CredentialFingerprint: not null } =>
            new AuthAuditActor(null, $"{auth.AuthType}:{auth.CredentialFingerprint}"),
        _ => new AuthAuditActor(null, auth.AuthType.ToString()),
    };
}

/// <summary>
/// Service for recording authentication and authorization audit events.
/// </summary>
public interface IAuthAuditService
{
    /// <summary>
    /// Log an authentication or authorization event.
    /// </summary>
    /// <param name="eventType">One of the <c>AuthAuditEventType</c> constants.</param>
    /// <param name="subjectId">The subject the event happened to, if known.</param>
    /// <param name="success">Whether the event succeeded.</param>
    /// <param name="ipAddress">Client IP address.</param>
    /// <param name="userAgent">Client user-agent string.</param>
    /// <param name="errorMessage">Error message on failure.</param>
    /// <param name="detailsJson">Additional details as a JSON string (stored as jsonb).</param>
    /// <param name="refreshTokenId">Related refresh token, if applicable.</param>
    /// <param name="actor">
    /// Who performed the action, when that is someone other than <paramref name="subjectId"/>.
    /// Leave null where the subject acted for themselves.
    /// </param>
    /// <param name="tenantId">The tenant the action targeted, if any.</param>
    Task LogAsync(string eventType, Guid? subjectId, bool success,
        string? ipAddress = null, string? userAgent = null,
        string? errorMessage = null, string? detailsJson = null,
        Guid? refreshTokenId = null,
        AuthAuditActor? actor = null,
        Guid? tenantId = null);
}

using Nocturne.Core.Models.Authorization;

namespace Nocturne.Core.Contracts.Auth;

/// <summary>
/// Who performed an audited action, when that is not the subject the action was performed on.
/// </summary>
/// <param name="SubjectId">The acting subject, when the caller has one of its own.</param>
/// <param name="Credential">
/// Identifies the acting credential for callers with no subject, e.g.
/// <c>InstanceKey:&lt;fingerprint&gt;</c> or <c>Guest:&lt;grant id&gt;</c>. Never carries key
/// material; see <see cref="AuthContext.CredentialFingerprint"/>.
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
        { TokenId: not null } => new AuthAuditActor(null, $"{auth.AuthType}:{auth.TokenId}"),
        _ => new AuthAuditActor(null, auth.AuthType.ToString()),
    };

    /// <summary>
    /// The caller behind <paramref name="auth"/> when they are distinguishable from
    /// <paramref name="subjectId"/>, otherwise null.
    /// </summary>
    /// <remarks>
    /// A credential with no subject of its own is always worth naming, since it can never be the
    /// subject the event happened to: <see cref="From"/> identifies it by its fingerprint, or by
    /// the grant it authenticated with when it has no fingerprint (a guest session). Only an
    /// unauthenticated request has no caller to name — a login carries
    /// <see cref="AuthContext.Unauthenticated"/> until it succeeds — so it yields null and leaves
    /// the subject as its own actor.
    /// </remarks>
    public static AuthAuditActor? FromCallerOtherThan(AuthContext? auth, Guid? subjectId) =>
        auth is { IsAuthenticated: true } && (auth.SubjectId is null || auth.SubjectId != subjectId)
            ? From(auth)
            : null;
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
    /// Defaults to the caller on the current request per
    /// <see cref="AuthAuditActor.FromCallerOtherThan"/>; pass one explicitly only where the actor
    /// is not that caller.
    /// </param>
    /// <param name="tenantId">
    /// The tenant the action targeted. Defaults to the tenant the request is pinned to; pass one
    /// explicitly for an action against a tenant other than the caller's own.
    /// </param>
    Task LogAsync(string eventType, Guid? subjectId, bool success,
        string? ipAddress = null, string? userAgent = null,
        string? errorMessage = null, string? detailsJson = null,
        Guid? refreshTokenId = null,
        AuthAuditActor? actor = null,
        Guid? tenantId = null);
}

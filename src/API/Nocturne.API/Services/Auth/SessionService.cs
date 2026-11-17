using Nocturne.Core.Contracts.Auth;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Orchestrates first-party session lifecycle by composing
/// <see cref="IJwtService"/>, <see cref="ISubjectService"/>, and
/// <see cref="IRefreshTokenService"/> — no direct database access.
/// </summary>
public class SessionService : ISessionService
{
    private readonly IJwtService _jwtService;
    private readonly ISubjectService _subjectService;
    private readonly IRefreshTokenService _refreshTokenService;

    public SessionService(
        IJwtService jwtService,
        ISubjectService subjectService,
        IRefreshTokenService refreshTokenService)
    {
        _jwtService = jwtService;
        _subjectService = subjectService;
        _refreshTokenService = refreshTokenService;
    }

    /// <inheritdoc />
    public async Task<SessionTokenPair> IssueSessionAsync(
        Guid subjectId, SessionContext context, CancellationToken ct = default)
    {
        var subject = await _subjectService.GetSubjectByIdAsync(subjectId)
            ?? throw new InvalidOperationException($"Subject {subjectId} not found.");

        var roles = await _subjectService.GetSubjectRolesAsync(subjectId);
        var permissions = await _subjectService.GetSubjectPermissionsAsync(subjectId);

        var subjectInfo = new SubjectInfo
        {
            Id = subject.Id,
            Name = subject.Name,
            Email = subject.Email,
        };

        // Tag the session with a stable id so a token chain can be revoked on its own.
        // Prefer the identity provider's session id (`sid`) when present — it ties us to the
        // provider's session for front-channel logout — otherwise mint our own. Most providers
        // (e.g. Google) omit `sid`, and the passkey/TOTP/setup paths have none, so without this
        // the id would be null and reuse detection could only revoke a subject's entire fleet.
        var oidcSessionId = string.IsNullOrEmpty(context.OidcSessionId)
            ? Guid.CreateVersion7().ToString("N")
            : context.OidcSessionId;

        var accessToken = _jwtService.GenerateAccessToken(subjectInfo, permissions, roles, oidcSessionId);
        var refreshToken = await _refreshTokenService.CreateRefreshTokenAsync(
            subjectId, oidcSessionId, context.DeviceDescription,
            context.IpAddress, context.UserAgent);
        var expiresIn = (int)_jwtService.GetAccessTokenLifetime().TotalSeconds;

        return new SessionTokenPair(accessToken, refreshToken, expiresIn, subjectId);
    }

    /// <inheritdoc />
    public async Task<SessionTokenPair?> RotateSessionAsync(
        string refreshToken, SessionContext context, CancellationToken ct = default)
    {
        if (await _refreshTokenService.FindOwnerAsync(refreshToken) is not { } owner)
            return null;

        if (await ReadRefreshClaimsAsync(refreshToken, owner.SubjectId, ct) is not { } claims)
            return null;

        var accessToken = _jwtService.GenerateAccessToken(
            claims.Subject, claims.Permissions, claims.Roles, owner.SessionId);
        var expiresIn = (int)_jwtService.GetAccessTokenLifetime().TotalSeconds;

        var newRefreshToken = await _refreshTokenService.RotateRefreshTokenAsync(
            refreshToken, context.IpAddress, context.UserAgent);

        if (newRefreshToken is null)
            return null;

        return new SessionTokenPair(accessToken, newRefreshToken, expiresIn, owner.SubjectId);
    }

    /// <inheritdoc />
    public async Task<SessionClaims?> ReadRefreshClaimsAsync(
        string refreshToken, Guid subjectId, CancellationToken ct = default)
    {
        var subject = await _subjectService.GetSubjectByIdAsync(subjectId);
        if (subject is not { IsActive: true })
        {
            await _refreshTokenService.RevokeRefreshTokenAsync(refreshToken, "Subject inactive");
            return null;
        }

        var roles = await _subjectService.GetSubjectRolesAsync(subjectId);
        var permissions = await _subjectService.GetSubjectPermissionsAsync(subjectId);

        return new SessionClaims(
            new SubjectInfo { Id = subject.Id, Name = subject.Name, Email = subject.Email },
            roles,
            permissions);
    }
}

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

        var accessToken = _jwtService.GenerateAccessToken(subjectInfo, permissions, roles);
        var refreshToken = await _refreshTokenService.CreateRefreshTokenAsync(
            subjectId, oidcSessionId, context.DeviceDescription,
            context.IpAddress, context.UserAgent);
        var expiresIn = (int)_jwtService.GetAccessTokenLifetime().TotalSeconds;

        return new SessionTokenPair(accessToken, refreshToken, expiresIn);
    }

    /// <inheritdoc />
    public async Task<SessionTokenPair?> RotateSessionAsync(
        string refreshToken, SessionContext context, CancellationToken ct = default)
    {
        var newRefreshToken = await _refreshTokenService.RotateRefreshTokenAsync(
            refreshToken, context.IpAddress, context.UserAgent);

        if (newRefreshToken is null)
            return null;

        var subjectId = await _refreshTokenService.ValidateRefreshTokenAsync(newRefreshToken);

        if (subjectId is null)
            return null;

        var subject = await _subjectService.GetSubjectByIdAsync(subjectId.Value)
            ?? throw new InvalidOperationException($"Subject {subjectId} not found.");

        var roles = await _subjectService.GetSubjectRolesAsync(subjectId.Value);
        var permissions = await _subjectService.GetSubjectPermissionsAsync(subjectId.Value);

        var subjectInfo = new SubjectInfo
        {
            Id = subject.Id,
            Name = subject.Name,
            Email = subject.Email,
        };

        var accessToken = _jwtService.GenerateAccessToken(subjectInfo, permissions, roles);
        var expiresIn = (int)_jwtService.GetAccessTokenLifetime().TotalSeconds;

        return new SessionTokenPair(accessToken, newRefreshToken, expiresIn);
    }
}

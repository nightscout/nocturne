using System.Security.Cryptography;
using System.Text;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Security;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Manages guest access links backed by grants with <c>grant_type = "guest"</c>. Codes are short
/// alphanumeric strings (ABC-DEFG format) hashed with SHA-256 before storage.
/// </summary>
public class GuestLinkService : IGuestLinkService
{
    private static readonly TimeSpan LinkLifetime = TimeSpan.FromHours(48);
    private const int MaxActiveLinks = 5;
    private const string CodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    private const int CodeLength = 7;

    /// <summary>
    /// What a guest link gets when its creator chooses nothing. It includes
    /// <see cref="Scope.AlertsRead"/> so a guest sees an alarm that is firing.
    /// </summary>
    private static readonly List<string> DefaultScopes =
        [Scope.HealthRead, Scope.TherapyRead, Scope.ReportsRead, Scope.AlertsRead];

    /// <summary>
    /// The guest vocabulary as categories to pick from: <see cref="Scope.AllowedGuestScopes"/> without
    /// the <see cref="Scope.HealthRead"/> alias, which is a shorthand for others in the list, and
    /// without <see cref="Scope.IdentityRead"/>, which no guest view reads.
    /// </summary>
    private static readonly IReadOnlyList<string> SelectableScopes = Scope.AllowedGuestScopes
        .Where(scope => scope is not Scope.HealthRead and not Scope.IdentityRead)
        .Order(StringComparer.Ordinal)
        .ToList();

    /// <summary>The categories <paramref name="creatorScopes"/> may put on a guest link.</summary>
    public static GuestLinkScopeOptions ScopeOptions(IReadOnlySet<string> creatorScopes)
    {
        var available = SelectableScopes.Where(scope => Scope.Satisfies(creatorScopes, scope)).ToList();
        var defaults = Scope.NormalizeGuest(DefaultScopes);

        return new GuestLinkScopeOptions(available, available.Where(defaults.Contains).ToList());
    }

    private readonly INocturneDbContextFactory _contexts;
    private readonly IOAuthGrantRepository _grants;
    private readonly GuestSessionCacheService _sessionCache;
    private readonly GrantRevocationService _grantRevocation;
    private readonly ILogger<GuestLinkService> _logger;

    /// <param name="contexts">Opens the unit of work a revoke runs on.</param>
    /// <param name="grants">The stored grants.</param>
    public GuestLinkService(
        INocturneDbContextFactory contexts,
        IOAuthGrantRepository grants,
        GuestSessionCacheService sessionCache,
        GrantRevocationService grantRevocation,
        ILogger<GuestLinkService> logger)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        _grants = grants ?? throw new ArgumentNullException(nameof(grants));
        _sessionCache = sessionCache;
        _grantRevocation = grantRevocation;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<GuestLinkCreationResult> CreateGuestLinkAsync(
        Guid dataOwnerSubjectId,
        Guid createdBySubjectId,
        string label,
        string baseUrl,
        IReadOnlySet<string> creatorScopes,
        IEnumerable<string>? scopes = null,
        bool limitTo24Hours = false,
        CancellationToken ct = default)
    {
        var requestedScopes = ResolveScopes(scopes, creatorScopes);

        var scopeList = Scope.ValidateGrantScopes(requestedScopes, OAuthGrantTypes.Guest);

        var activeCount = await GetActiveCountAsync(dataOwnerSubjectId, ct);
        if (activeCount >= MaxActiveLinks)
        {
            throw new InvalidOperationException(
                $"Maximum of {MaxActiveLinks} active guest links reached for this data owner.");
        }

        var code = GenerateCode();
        var hash = CredentialHash.GuestCode(code);
        var now = DateTime.UtcNow;

        var grantId = await _grants.CreateAsync(
            new NewOAuthGrant(
                ClientEntityId: null,
                dataOwnerSubjectId,
                OAuthGrantTypes.Guest,
                scopeList,
                label,
                TokenHash: hash,
                LegacySecretHash: null,
                ExpiresAt: now + LinkLifetime,
                limitTo24Hours,
                CreatedBySubjectId: createdBySubjectId),
            ct);
        var grant = await _grants.GetAsync(grantId, ct)
            ?? throw new InvalidOperationException($"Guest link {grantId} is not readable after its save.");

        var info = MapToInfo(grant);
        var formatted = FormatCode(code);
        var fullUrl = $"{baseUrl.TrimEnd('/')}/guest/{formatted}";

        _logger.LogInformation(
            "Guest link created for data owner {DataOwnerSubjectId} by {CreatedBySubjectId}, expires {ExpiresAt}",
            dataOwnerSubjectId, createdBySubjectId, grant.ExpiresAt);

        return new GuestLinkCreationResult(formatted, fullUrl, info);
    }

    /// <summary>
    /// The scopes to store on a new guest link. Explicit scopes are checked against the creator's
    /// own (see <see cref="Scope.ValidateDelegation"/>); with none, <see cref="DefaultScopes"/> are
    /// expanded, cut to the guest vocabulary, and narrowed to what the creator holds.
    /// </summary>
    private static IEnumerable<string> ResolveScopes(
        IEnumerable<string>? scopes, IReadOnlySet<string> creatorScopes)
    {
        if (scopes is not null && scopes.Any())
        {
            var violation = Scope.ValidateDelegation(scopes, creatorScopes);
            if (violation is not null)
                throw new GrantCeilingViolationException(violation);

            return scopes;
        }

        var narrowed = Scope.NormalizeGuest(DefaultScopes)
            .Where(scope => Scope.Satisfies(creatorScopes, scope))
            .ToHashSet(StringComparer.Ordinal);

        if (narrowed.Count == 0)
        {
            throw new GrantCeilingViolationException(new GrantCeilingViolation(
                GrantCeilingViolation.ExceedsGranter,
                "None of the default guest scopes are available to this account."));
        }

        return narrowed;
    }

    /// <inheritdoc />
    public async Task<GuestLinkActivationResult> ActivateAsync(
        string code,
        string? ipAddress,
        string? userAgent,
        CancellationToken ct = default)
    {
        var normalized = code.Replace("-", "").ToUpperInvariant();
        var hash = CredentialHash.GuestCode(normalized);
        var now = DateTime.UtcNow;

        var grant = await _grants.ActivateGuestAsync(hash, now, new GrantPresentation(ipAddress, userAgent), ct);

        if (grant is null)
        {
            return new GuestLinkActivationResult(false, null, "Invalid or expired code");
        }

        _logger.LogInformation("Guest link {GrantId} activated from {IpAddress}", grant.Id, ipAddress);

        return new GuestLinkActivationResult(true, ToSession(grant), null);
    }

    /// <inheritdoc />
    public async Task<GuestSessionInfo?> ValidateSessionAsync(Guid grantId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        var grant = await FindGuestAsync(_grants, grantId, ct);

        if (grant is null || grant.IsRevoked || !(grant.ExpiresAt > now) || grant.ActivatedAt is null)
            return null;

        return ToSession(grant);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GuestLinkInfo>> GetGuestLinksAsync(
        Guid dataOwnerSubjectId,
        bool includeDismissed = false,
        CancellationToken ct = default)
    {
        var grants = await _grants.GetGuestLinksAsync(dataOwnerSubjectId, includeDismissed, ct);

        return grants.Select(MapToInfo).ToList().AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<bool> RevokeAsync(Guid grantId, Guid requestingSubjectId, CancellationToken ct = default)
    {
        await using var unit = await DbUnitOfWork.OpenAsync(_contexts, ct);
        var grant = await FindGuestAsync(new OAuthGrantRepository(unit), grantId, ct);

        if (grant is null || grant.IsRevoked) return false;

        if (grant.SubjectId != requestingSubjectId && grant.CreatedBySubjectId != requestingSubjectId)
        {
            _logger.LogWarning(
                "Subject {RequestingSubjectId} attempted to revoke guest grant {GrantId} owned by {SubjectId}",
                requestingSubjectId, grantId, grant.SubjectId);
            return false;
        }

        await _grantRevocation.StageAsync(unit, grant, ct);
        await unit.CommitAsync(token => unit.Context.SaveChangesAsync(token), ct);
        _grantRevocation.EvictSessions([grant]);

        _logger.LogInformation(
            "Guest link {GrantId} revoked by {RequestingSubjectId}", grantId, requestingSubjectId);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> DismissAsync(Guid grantId, Guid requestingSubjectId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        var grant = await FindGuestAsync(_grants, grantId, ct);

        if (grant is null || grant.DismissedAt is not null) return false;

        if (grant.SubjectId != requestingSubjectId && grant.CreatedBySubjectId != requestingSubjectId)
        {
            _logger.LogWarning(
                "Subject {RequestingSubjectId} attempted to dismiss guest grant {GrantId} owned by {SubjectId}",
                requestingSubjectId, grantId, grant.SubjectId);
            return false;
        }

        var isTerminal = grant.RevokedAt.HasValue || grant.ExpiresAt <= now;
        if (!isTerminal)
        {
            _logger.LogWarning(
                "Subject {RequestingSubjectId} attempted to dismiss non-terminal guest grant {GrantId}",
                requestingSubjectId, grantId);
            return false;
        }

        await _grants.DismissAsync(grant.Id, now, ct);

        _sessionCache.Evict(grant.TenantId, grant.Id);

        _logger.LogInformation("Guest link {GrantId} dismissed by {RequestingSubjectId}", grantId, requestingSubjectId);
        return true;
    }

    /// <inheritdoc />
    public Task<int> GetActiveCountAsync(Guid dataOwnerSubjectId, CancellationToken ct = default) =>
        _grants.CountLiveGuestLinksAsync(dataOwnerSubjectId, DateTime.UtcNow, ct);

    /// <inheritdoc />
    public Task<bool> HasRedeemableCodeAsync(CancellationToken ct = default) =>
        _grants.HasRedeemableGuestCodeAsync(DateTime.UtcNow, ct);

    /// <summary>The grant <paramref name="grantId"/> if it is a guest link, in any state.</summary>
    private static async Task<OAuthGrantRecord?> FindGuestAsync(IOAuthGrantRepository grants, Guid grantId, CancellationToken ct)
    {
        var grant = await grants.GetAsync(grantId, ct);
        return grant is { GrantType: OAuthGrantTypes.Guest } ? grant : null;
    }

    private static string GenerateCode()
    {
        Span<byte> bytes = stackalloc byte[CodeLength];
        RandomNumberGenerator.Fill(bytes);

        var sb = new StringBuilder(CodeLength);
        for (var i = 0; i < CodeLength; i++)
        {
            sb.Append(CodeAlphabet[bytes[i] % CodeAlphabet.Length]);
        }

        return sb.ToString();
    }

    private static string FormatCode(string code)
    {
        return $"{code[..3]}-{code[3..]}";
    }

    private static GuestSessionInfo ToSession(OAuthGrantRecord grant) =>
        new(
            grant.Id,
            grant.TenantId,
            grant.SubjectId,
            grant.Scopes,
            grant.Label,
            grant.ExpiresAt!.Value,
            grant.LimitTo24Hours);

    private static GuestLinkInfo MapToInfo(OAuthGrantRecord grant)
    {
        var now = DateTime.UtcNow;
        var status = grant.RevokedAt.HasValue
            ? GuestLinkStatus.Revoked
            : grant.ExpiresAt <= now
                ? GuestLinkStatus.Expired
                : grant.ActivatedAt.HasValue
                    ? GuestLinkStatus.Active
                    : GuestLinkStatus.Pending;

        return new GuestLinkInfo
        {
            Id = grant.Id,
            DataOwnerSubjectId = grant.SubjectId,
            CreatedBySubjectId = grant.CreatedBySubjectId ?? grant.SubjectId,
            Label = grant.Label ?? string.Empty,
            Scopes = Scope.ForDisplay(grant.Scopes, grant.GrantType),
            CreatedAt = grant.CreatedAt,
            ExpiresAt = grant.ExpiresAt ?? grant.CreatedAt + LinkLifetime,
            ActivatedAt = grant.ActivatedAt,
            ActivatedIp = grant.ActivatedIp,
            RevokedAt = grant.RevokedAt,
            DismissedAt = grant.DismissedAt,
            Status = status,
        };
    }
}

using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// <see cref="IOAuthGrantRepository"/> over <see cref="NocturneDbContext"/>: one context per call,
/// saving each write, or the caller's <see cref="DbUnitOfWork"/> when constructed over one.
/// </summary>
/// <remarks>
/// Registered transient, like the factory it holds: the authentication handlers are singletons, and
/// resolving the repository from the root gives it the root factory, which reads as no one and is
/// pinned per call on the handlers' reads.
/// </remarks>
public class OAuthGrantRepository : IOAuthGrantRepository
{
    private readonly INocturneDbContextFactory? _contexts;
    private readonly DbUnitOfWork? _unit;

    /// <summary>A repository whose calls each open their own context and save what they write.</summary>
    public OAuthGrantRepository(INocturneDbContextFactory contexts)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
    }

    /// <summary>
    /// A repository whose calls run on <paramref name="unit"/>, which the caller owns. A write is
    /// staged on the unit and not saved: the owner saves it, so a grant's revocation lands in one
    /// transaction with its tokens' and its devices'. A read tracks its rows, so a write on the same
    /// unit edits the rows it read in place.
    /// </summary>
    public OAuthGrantRepository(DbUnitOfWork unit)
    {
        _unit = unit ?? throw new ArgumentNullException(nameof(unit));
    }

    private ValueTask<DbContextLease> OpenAsync(CancellationToken ct) => DbContextLease.OpenAsync(_unit, _contexts, ct);

    private async ValueTask<NocturneDbContext> OpenForTenantAsync(Guid tenantId, CancellationToken ct) =>
        await (_contexts ?? throw new InvalidOperationException(
                "A repository over a unit of work runs on the unit's tenant and cannot pin another."))
            .CreateForTenantAsync(tenantId, ct);

    private Task SaveAsync(NocturneDbContext db, CancellationToken ct) =>
        _unit is null ? db.SaveChangesAsync(ct) : Task.CompletedTask;

    private IQueryable<OAuthGrantEntity> Grants(NocturneDbContext db) =>
        _unit is null ? db.OAuthGrants.AsNoTracking() : db.OAuthGrants;

    private IQueryable<OAuthGrantEntity> GrantsWithClient(NocturneDbContext db) => Grants(db).Include(g => g.Client);

    /// <inheritdoc/>
    public virtual async Task<AuthenticatedDirectGrant?> FindLiveDirectGrantAsync(
        Guid tenantId, DirectGrantLookup lookup, DateTime now, CancellationToken ct)
    {
        await using var db = await OpenForTenantAsync(tenantId, ct);
        var grants = Match(PinnedGrants(db, tenantId).Where(IsLiveDirectGrant(now)), lookup);
        var row = await WithMembership(grants, db, tenantId).FirstOrDefaultAsync(ct);
        return row is null ? null : new AuthenticatedDirectGrant(row.Grant, row.Membership());
    }

    /// <inheritdoc/>
    public virtual async Task<OAuthGrantState?> GetStateAsync(Guid tenantId, Guid grantId, CancellationToken ct)
    {
        await using var db = await OpenForTenantAsync(tenantId, ct);
        return await PinnedGrants(db, tenantId)
            .Where(g => g.Id == grantId)
            .Select(g => new OAuthGrantState(g.RevokedAt, g.ExpiresAt, g.Scopes, g.LimitTo24Hours))
            .FirstOrDefaultAsync(ct);
    }

    /// <inheritdoc/>
    public virtual async Task RecordUseAsync(
        Guid tenantId, Guid grantId, DateTime now, GrantPresentation? presentation, CancellationToken ct)
    {
        await using var db = await OpenForTenantAsync(tenantId, ct);
        await StampUseAsync(PinnedGrants(db, tenantId).Where(g => g.Id == grantId), now, presentation, ct);
    }

    /// <inheritdoc/>
    public virtual async Task RecordUseAsync(
        Guid grantId, DateTime now, GrantPresentation? presentation, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        await StampUseAsync(lease.Context.OAuthGrants.Where(g => g.Id == grantId), now, presentation, ct);
    }

    /// <inheritdoc/>
    public virtual async Task<OAuthGrantRecord?> GetAsync(Guid grantId, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        return ToRecord(await GrantsWithClient(lease.Context).FirstOrDefaultAsync(g => g.Id == grantId, ct));
    }

    /// <inheritdoc/>
    public virtual async Task<OAuthGrantRecord?> FindActiveAsync(Guid clientEntityId, Guid subjectId, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        return ToRecord(await GrantsWithClient(lease.Context)
            .Where(g => g.ClientEntityId == clientEntityId && g.SubjectId == subjectId && g.RevokedAt == null)
            .FirstOrDefaultAsync(ct));
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<OAuthGrantRecord>> GetActiveForSubjectAsync(Guid subjectId, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        return ToRecords(await GrantsWithClient(lease.Context)
            .Where(g => g.SubjectId == subjectId && g.RevokedAt == null)
            .OrderByDescending(g => g.CreatedAt)
            .ToListAsync(ct));
    }

    /// <inheritdoc/>
    public virtual async Task<OAuthGrantRecord?> FindOfSubjectAsync(Guid grantId, Guid ownerSubjectId, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        return ToRecord(await GrantsWithClient(lease.Context)
            .Where(g => g.Id == grantId && g.SubjectId == ownerSubjectId)
            .FirstOrDefaultAsync(ct));
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<OAuthGrantRecord>> ListDirectAsync(
        Guid? subjectId, DateTime? liveAt, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var grants = Grants(lease.Context)
            .Where(g => (subjectId == null || g.SubjectId == subjectId.Value)
                     && g.GrantType == OAuthGrantTypes.Direct
                     && g.RevokedAt == null);
        if (liveAt is { } now)
            grants = grants.Where(IsLiveDirectGrant(now));

        return ToRecords(await grants.OrderByDescending(g => g.CreatedAt).ToListAsync(ct));
    }

    /// <inheritdoc/>
    public virtual async Task<OAuthGrantRecord?> FindLiveDirectGrantAsync(
        DirectGrantLookup lookup, DateTime now, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        return ToRecord(await Match(Grants(lease.Context).Where(IsLiveDirectGrant(now)), lookup).FirstOrDefaultAsync(ct));
    }

    /// <inheritdoc/>
    public virtual async Task<bool> HasUnrevokedLegacySecretAsync(string legacySecretHash, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        return await lease.Context.OAuthGrants.AnyAsync(
            g => g.LegacySecretHash == legacySecretHash
                 && g.GrantType == OAuthGrantTypes.Direct
                 && g.RevokedAt == null, ct);
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlySet<string>> GetTokenHashesAsync(CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        return await lease.Context.OAuthGrants
            .Where(g => g.TokenHash != null)
            .Select(g => g.TokenHash!)
            .ToHashSetAsync(ct);
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<OAuthGrantRecord>> GetGuestLinksAsync(
        Guid dataOwnerSubjectId, bool includeDismissed, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var query = Grants(lease.Context)
            .Where(g => g.SubjectId == dataOwnerSubjectId && g.GrantType == OAuthGrantTypes.Guest);
        if (!includeDismissed)
            query = query.Where(g => g.DismissedAt == null);

        return ToRecords(await query.OrderByDescending(g => g.CreatedAt).ToListAsync(ct));
    }

    /// <inheritdoc/>
    public virtual async Task<int> CountLiveGuestLinksAsync(Guid dataOwnerSubjectId, DateTime now, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        return await lease.Context.OAuthGrants.CountAsync(
            g => g.SubjectId == dataOwnerSubjectId
                 && g.GrantType == OAuthGrantTypes.Guest
                 && g.RevokedAt == null
                 && g.ExpiresAt > now, ct);
    }

    /// <inheritdoc/>
    public virtual async Task<bool> HasRedeemableGuestCodeAsync(DateTime now, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        return await lease.Context.OAuthGrants.AnyAsync(
            g => g.GrantType == OAuthGrantTypes.Guest
                 && g.RevokedAt == null
                 && g.ActivatedAt == null
                 && g.ExpiresAt > now, ct);
    }

    /// <inheritdoc/>
    public virtual async Task<bool> HasLiveShareGrantAsync(DateTime now, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        return await lease.Context.OAuthGrants.AnyAsync(
            g => (g.GrantType == OAuthGrantTypes.Guest || g.GrantType == OAuthGrantTypes.Follower)
                 && g.RevokedAt == null
                 && (g.ExpiresAt == null || g.ExpiresAt > now), ct);
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyDictionary<Guid, string?>> GetClientDisplayNamesAsync(
        IReadOnlyCollection<Guid> grantIds, CancellationToken ct)
    {
        if (grantIds.Count == 0)
            return new Dictionary<Guid, string?>();

        await using var lease = await OpenAsync(ct);
        var ids = grantIds.ToList();
        return await lease.Context.OAuthGrants
            .Where(g => ids.Contains(g.Id))
            .Select(g => new { g.Id, Name = g.Client!.DisplayName })
            .ToDictionaryAsync(g => g.Id, g => g.Name, ct);
    }

    /// <inheritdoc/>
    public virtual async Task<Guid> CreateAsync(NewOAuthGrant grant, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;
        var entity = new OAuthGrantEntity
        {
            Id = Guid.CreateVersion7(),
            ClientEntityId = grant.ClientEntityId,
            SubjectId = grant.SubjectId,
            CreatedBySubjectId = grant.CreatedBySubjectId,
            GrantType = grant.GrantType,
            Scopes = grant.Scopes.ToList(),
            Label = grant.Label,
            TokenHash = grant.TokenHash,
            LegacySecretHash = grant.LegacySecretHash,
            ExpiresAt = grant.ExpiresAt,
            LimitTo24Hours = grant.LimitTo24Hours,
            ApprovedByDeviceCode = grant.ApprovedByDeviceCode,
        };
        db.OAuthGrants.Add(entity);
        await SaveAsync(db, ct);
        return entity.Id;
    }

    /// <inheritdoc/>
    public virtual async Task<Guid> AdoptLegacyCredentialAsync(AdoptedLegacyCredential credential, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;
        var entity = OAuthGrantEntity.AdoptedLegacyCredential(
            credential.SubjectId,
            credential.Label,
            credential.Scopes,
            credential.TokenHash,
            credential.LegacySecretHash,
            credential.LegacyTokenDigest);
        db.OAuthGrants.Add(entity);
        await SaveAsync(db, ct);
        return entity.Id;
    }

    /// <inheritdoc/>
    public virtual async Task<OAuthGrantRecord?> UpdateAsync(Guid grantId, OAuthGrantEdit edit, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;
        var entity = await db.OAuthGrants.Include(g => g.Client).FirstOrDefaultAsync(g => g.Id == grantId, ct);
        if (entity is null)
            return null;

        if (edit.Label is not null)
            entity.Label = edit.Label;
        if (edit.Scopes is not null)
            entity.Scopes = edit.Scopes.ToList();
        if (edit.LimitTo24Hours is { } limit)
            entity.LimitTo24Hours = limit;
        entity.ApprovedByDeviceCode |= edit.ApprovedByDeviceCode;

        await SaveAsync(db, ct);
        return ToRecord(entity);
    }

    /// <inheritdoc/>
    public virtual async Task<OAuthGrantRecord?> ActivateGuestAsync(
        string codeHash, DateTime now, GrantPresentation presentation, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;
        var entity = await db.OAuthGrants.FirstOrDefaultAsync(
            g => g.TokenHash == codeHash
                 && g.GrantType == OAuthGrantTypes.Guest
                 && g.RevokedAt == null
                 && g.ActivatedAt == null
                 && g.ExpiresAt > now, ct);
        if (entity is null)
            return null;

        entity.ActivatedAt = now;
        entity.ActivatedIp = presentation.IpAddress;
        entity.ActivatedUserAgent = presentation.UserAgent;
        await SaveAsync(db, ct);
        return ToRecord(entity);
    }

    /// <inheritdoc/>
    public virtual async Task<bool> DismissAsync(Guid grantId, DateTime now, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;
        var entity = await db.OAuthGrants.FirstOrDefaultAsync(g => g.Id == grantId, ct);
        if (entity is null)
            return false;

        entity.DismissedAt = now;
        await SaveAsync(db, ct);
        return true;
    }

    /// <inheritdoc/>
    public virtual async Task<int> RevokeAsync(IReadOnlyCollection<Guid> grantIds, DateTime now, CancellationToken ct)
    {
        if (grantIds.Count == 0)
            return 0;

        await using var lease = await OpenAsync(ct);
        var db = lease.Context;
        var ids = grantIds.ToList();
        var grants = await db.OAuthGrants
            .Where(g => ids.Contains(g.Id) && g.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var grant in grants)
            grant.RevokedAt = now;

        await SaveAsync(db, ct);
        return grants.Count;
    }

    /// <summary>
    /// The grants of <paramref name="tenantId"/> on a context pinned to it. The query filter is
    /// ignored because the pin, not the scope, names the tenant, and the tenant is matched explicitly
    /// for the reason on <see cref="IOAuthGrantRepository.FindLiveDirectGrantAsync(Guid, DirectGrantLookup, DateTime, CancellationToken)"/>.
    /// </summary>
    private static IQueryable<OAuthGrantEntity> PinnedGrants(NocturneDbContext db, Guid tenantId) =>
        db.OAuthGrants.AsNoTracking().IgnoreQueryFilters().Where(g => g.TenantId == tenantId);

    /// <summary>
    /// What makes a direct grant usable at <paramref name="now"/>. Every read that authenticates or
    /// exchanges one goes through it, and <see cref="OAuthGrantRecord.IsLiveDirectGrantAt"/> is the
    /// same rule for a record already read, so none can accept a grant another refuses.
    /// </summary>
    private static Expression<Func<OAuthGrantEntity, bool>> IsLiveDirectGrant(DateTime now) =>
        g => g.GrantType == OAuthGrantTypes.Direct
             && g.RevokedAt == null
             && (g.ExpiresAt == null || g.ExpiresAt > now);

    /// <remarks>
    /// The digest match is a filtered-index range scan only an imported Nightscout token can
    /// satisfy; the callers keep it a separate lookup from the hash match so the planner is not
    /// denied the digest index on every request.
    /// </remarks>
    private static IQueryable<OAuthGrantEntity> Match(IQueryable<OAuthGrantEntity> grants, DirectGrantLookup lookup) =>
        lookup switch
        {
            DirectGrantLookup.ByTokenHash { Hashes: [var hash] } => grants.Where(g => g.TokenHash == hash),
            DirectGrantLookup.ByTokenHash { Hashes: var hashes } =>
                grants.Where(g => g.TokenHash != null && hashes.Contains(g.TokenHash)),
            DirectGrantLookup.ByLegacySecretHash { Hash: var hash } => grants.Where(g => g.LegacySecretHash == hash),
            DirectGrantLookup.ByLegacyTokenDigestPrefix { Prefix: var prefix } =>
                grants.Where(g => g.LegacyTokenDigest != null && g.LegacyTokenDigest.StartsWith(prefix)),
            _ => throw new ArgumentOutOfRangeException(nameof(lookup)),
        };

    /// <summary>
    /// Stamps the use in one statement, and only when the stored stamp is due
    /// (<see cref="LastUsedStamp.StaleBefore"/>), so writes that race stamp once. The address and
    /// agent are written only when a presentation is given, so a caller that has none keeps the
    /// stored ones.
    /// </summary>
    private static Task<int> StampUseAsync(
        IQueryable<OAuthGrantEntity> grant, DateTime now, GrantPresentation? presentation, CancellationToken ct)
    {
        var staleBefore = LastUsedStamp.StaleBefore(now);
        return grant.Where(g => g.LastUsedAt == null || g.LastUsedAt < staleBefore).ExecuteUpdateAsync(s =>
        {
            s.SetProperty(g => g.LastUsedAt, now);
            if (presentation is not null)
            {
                s.SetProperty(g => g.LastUsedIp, presentation.IpAddress);
                s.SetProperty(g => g.LastUsedUserAgent, presentation.UserAgent);
            }
        }, ct);
    }

    /// <summary>
    /// Pairs each grant with its subject's membership of <paramref name="tenantId"/> as a left join,
    /// so an authenticating request reads both in one statement, as the host's
    /// <c>RequestMembership</c> does for a subject.
    /// </summary>
    private static IQueryable<AuthenticatedRow> WithMembership(
        IQueryable<OAuthGrantEntity> grants, NocturneDbContext db, Guid tenantId) =>
        grants
            .GroupJoin(
                db.TenantMembers.AsNoTracking().Where(m => m.TenantId == tenantId),
                g => g.SubjectId,
                m => m.SubjectId,
                (g, members) => new { g, members })
            .SelectMany(
                joined => joined.members.DefaultIfEmpty(),
                (joined, m) => new AuthenticatedRow
                {
                    Grant = new DirectGrantCredential(
                        joined.g.Id,
                        joined.g.SubjectId,
                        joined.g.Scopes,
                        joined.g.Label,
                        joined.g.LimitTo24Hours,
                        joined.g.LastUsedAt,
                        joined.g.IsMigrated,
                        joined.g.TokenHash,
                        joined.g.LegacySecretHash),
                    MemberId = (Guid?)m!.Id,
                    DirectPermissions = m.DirectPermissions,
                    RolePermissions = m.MemberRoles.Select(mr => mr.TenantRole.Permissions).ToList(),
                    MemberLimitTo24Hours = (bool?)m.LimitTo24Hours,
                    MemberLastUsedAt = m.LastUsedAt,
                });

    /// <summary>A grant with the membership columns of the left join; <see cref="MemberId"/> is null when there is none.</summary>
    private sealed class AuthenticatedRow
    {
        public required DirectGrantCredential Grant { get; init; }

        public Guid? MemberId { get; init; }

        public List<string>? DirectPermissions { get; init; }

        public List<List<string>> RolePermissions { get; init; } = [];

        public bool? MemberLimitTo24Hours { get; init; }

        public DateTime? MemberLastUsedAt { get; init; }

        public DirectGrantMembership? Membership() => MemberId is { } id
            ? new DirectGrantMembership(
                id,
                new TenantMemberAccess(
                    MemberPermissions.Effective(RolePermissions, DirectPermissions).ToHashSet(),
                    MemberLimitTo24Hours == true),
                MemberLastUsedAt)
            : null;
    }

    private static IReadOnlyList<OAuthGrantRecord> ToRecords(List<OAuthGrantEntity> grants) =>
        grants.Select(g => ToRecord(g)!).ToList();

    private static OAuthGrantRecord? ToRecord(OAuthGrantEntity? grant) =>
        grant is null ? null : new OAuthGrantRecord(
            grant.Id,
            grant.TenantId,
            grant.ClientEntityId,
            grant.Client is null ? null : new OAuthGrantClient(
                grant.Client.ClientId, grant.Client.DisplayName, grant.Client.ClientUri, grant.Client.LogoUri),
            grant.ClientIdentity,
            grant.SubjectId,
            grant.CreatedBySubjectId,
            grant.GrantType,
            grant.Scopes,
            grant.Label,
            grant.CreatedAt,
            grant.LastUsedAt,
            grant.LastUsedIp,
            grant.LastUsedUserAgent,
            grant.RevokedAt,
            grant.DismissedAt,
            grant.ExpiresAt,
            grant.ActivatedAt,
            grant.ActivatedIp,
            grant.ActivatedUserAgent,
            grant.LimitTo24Hours,
            grant.IsMigrated);
}

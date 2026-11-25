using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
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
    /// <summary>First key of the two-key advisory lock form for a consent of one subject to one client.</summary>
    private const int ConsentLockClass = 0x4E47_434E;

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
    public virtual Task<AuthenticatedDirectGrant?> FindLiveDirectGrantAsync(
        Guid tenantId, DirectGrantLookup lookup, DateTime now, CancellationToken ct) =>
        MembershipReasons.ReadInForceAsync(_contexts, tenantId, now, async (inForce, token) =>
        {
            var row = await FindLiveDirectGrantRowAsync(tenantId, lookup, now, inForce, token);
            return (row is null ? null : new AuthenticatedDirectGrant(row.Grant, row.Membership(now)),
                row?.Grant.SubjectId ?? Guid.Empty, row?.MemberNextReasonLapseAt);
        }, ct);

    private async Task<AuthenticatedRow?> FindLiveDirectGrantRowAsync(
        Guid tenantId, DirectGrantLookup lookup, DateTime now, Expression<Func<TenantMemberEntity, bool>> inForce,
        CancellationToken ct)
    {
        await using var db = await OpenForTenantAsync(tenantId, ct);
        var grants = Match(PinnedGrants(db, tenantId).Where(IsLiveDirectGrant(now)).Where(g => g.Subject!.IsActive), lookup);
        return await WithMembership(grants, db, tenantId, now, inForce).FirstOrDefaultAsync(ct);
    }

    /// <inheritdoc/>
    public virtual async Task<OAuthGrantState?> GetStateAsync(Guid tenantId, Guid grantId, CancellationToken ct)
    {
        await using var db = await OpenForTenantAsync(tenantId, ct);
        return await PinnedGrants(db, tenantId)
            .Where(g => g.Id == grantId)
            .Select(g => new OAuthGrantState(g.RevokedAt, g.ExpiresAt, g.Scopes, g.LimitTo24Hours, g.CreatedBySubjectId))
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
            .Where(g => g.ClientEntityId == clientEntityId
                && g.SubjectId == subjectId
                && g.GrantType == OAuthGrantTypes.App
                && g.RevokedAt == null)
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
    public virtual async Task<IReadOnlyList<OAuthGrantRecord>> ListForConnectionsAsync(
        Guid? subjectId, IReadOnlyCollection<Guid> writerIds, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        return ToRecords(await GrantsWithClient(lease.Context)
            .Where(g => (g.SubjectId == subjectId && g.RevokedAt == null) || writerIds.Contains(g.Id))
            .OrderByDescending(g => g.CreatedAt)
            .ToListAsync(ct));
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<OAuthGrantRecord>> ListAppGrantsOfClientsAsync(
        IReadOnlyCollection<Guid> clientEntityIds, IReadOnlyCollection<string> softwareIds, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        return ToRecords(await GrantsWithClient(lease.Context)
            .Where(g => g.GrantType == OAuthGrantTypes.App
                && ((g.ClientEntityId != null && clientEntityIds.Contains(g.ClientEntityId.Value))
                    || (g.Client != null && g.Client.SoftwareId != null && softwareIds.Contains(g.Client.SoftwareId))))
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
    public virtual async Task<OAuthGrantRecord?> FindLiveLegacySecretAsync(
        string legacySecretHash, DateTime now, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        return ToRecord(await Grants(lease.Context)
            .Where(IsLiveDirectGrant(now))
            .Where(g => g.LegacySecretHash == legacySecretHash)
            .OrderBy(g => g.CreatedAt)
            .FirstOrDefaultAsync(ct));
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
    public virtual async Task<IReadOnlyList<OAuthGrantRecord>> GetLiveGuestLinksCreatedByAsync(
        Guid createdBySubjectId, DateTime now, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        return ToRecords(await LiveGuestLinksCreatedBy(Grants(lease.Context), createdBySubjectId, now)
            .OrderBy(g => g.CreatedAt)
            .ToListAsync(ct));
    }

    /// <summary>
    /// Lowers to <paramref name="notAfter"/> the expiry of every guest link and device credential
    /// <paramref name="createdBySubjectId"/> made that is live at <paramref name="now"/> and expires
    /// later or never, and stages a <c>guest_link_capped</c> or <c>device_credential_capped</c> audit row
    /// for each. A grant that expires sooner is left alone, and so is one whose revocation is already
    /// staged on the unit. Stages on the caller's unit of work and does not save.
    /// </summary>
    /// <returns>The grants it lowered, for the caller to evict their sessions once the unit commits.</returns>
    /// <exception cref="InvalidOperationException">The repository is not over a unit of work.</exception>
    public virtual async Task<IReadOnlyList<OAuthGrantRecord>> CapGrantsCreatedByAsync(
        Guid createdBySubjectId, DateTime notAfter, DateTime now, CancellationToken ct)
    {
        var unit = _unit ?? throw new InvalidOperationException(
            "Capping the grants a member made stages audit rows with the change, so it needs the caller's unit of work.");
        var db = unit.Context;
        var made = await LiveGrantsCreatedBy(db.OAuthGrants, createdBySubjectId, now)
            .Where(g => g.ExpiresAt == null || g.ExpiresAt > notAfter)
            .OrderBy(g => g.CreatedAt)
            .ToListAsync(ct);
        // A revocation staged earlier on the unit is not saved yet, so the store still reads its grant as live.
        var capped = made.Where(g => g.RevokedAt == null).ToList();
        foreach (var grant in capped)
        {
            grant.ExpiresAt = notAfter;
            MemberAccessAudit.StageGrantCapped(db, grant.GrantType, grant.TenantId, createdBySubjectId, grant.Id, notAfter);
        }

        return ToRecords(capped);
    }

    /// <inheritdoc/>
    public virtual async Task<bool> KeepDeviceCredentialAsync(Guid grantId, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;
        if (await db.FindDeviceSubjectOf(db.TenantId, ct) is not { } deviceSubjectId)
            return false;

        var grant = await db.OAuthGrants.FirstOrDefaultAsync(
            g => g.Id == grantId
                 && g.SubjectId == deviceSubjectId
                 && g.GrantType == OAuthGrantTypes.Direct
                 && g.RevokedAt == null
                 && g.CreatedBySubjectId != null
                 && g.CreatedBySubjectId != g.SubjectId, ct);
        if (grant is null)
            return false;

        MemberAccessAudit.StageDeviceCredentialKept(db, grant.TenantId, grant.CreatedBySubjectId, grant.Id);
        grant.ExpiresAt = null;
        // With no maker, no later change to that member's access can cap the credential again.
        grant.CreatedBySubjectId = null;
        await SaveAsync(db, ct);
        return true;
    }

    /// <inheritdoc/>
    public virtual void StageMemberRemovalGuestLinkAudit(IEnumerable<OAuthGrantRecord> links, Guid memberSubjectId)
    {
        var unit = _unit ?? throw new InvalidOperationException(
            "Auditing revoked guest links stages rows with the revocation, so it needs the caller's unit of work.");
        foreach (var link in links)
            MemberAccessAudit.StageGuestLinkRevoked(unit.Context, link.TenantId, memberSubjectId, link.Id);
    }

    private static IQueryable<OAuthGrantEntity> LiveGuestLinksCreatedBy(
        IQueryable<OAuthGrantEntity> grants, Guid createdBySubjectId, DateTime now) =>
        LiveGrantsCreatedBy(grants, createdBySubjectId, now).Where(g => g.GrantType == OAuthGrantTypes.Guest);

    /// <summary>
    /// The guest links and device credentials <paramref name="createdBySubjectId"/> made that are
    /// unrevoked and unexpired at <paramref name="now"/>.
    /// </summary>
    private static IQueryable<OAuthGrantEntity> LiveGrantsCreatedBy(
        IQueryable<OAuthGrantEntity> grants, Guid createdBySubjectId, DateTime now) =>
        grants.Where(g => (g.GrantType == OAuthGrantTypes.Guest || g.GrantType == OAuthGrantTypes.Direct)
                          && g.CreatedBySubjectId == createdBySubjectId
                          && g.RevokedAt == null
                          && (g.ExpiresAt == null || g.ExpiresAt > now));

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
            credential.LegacyTokenDigest,
            credential.CreatedBySubjectId,
            credential.ExpiresAt);
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

        Apply(entity, edit);
        await SaveAsync(db, ct);
        return ToRecord(entity);
    }

    /// <inheritdoc/>
    public virtual async Task<bool> LockConsentAsync(Guid clientEntityId, Guid subjectId, CancellationToken ct)
    {
        var db = OwnUnit("Serialising consents holds a lock to the commit of the caller's unit of work.").Context;
        await db.LockForTransactionAsync(ConsentLockClass, $"{db.TenantId}|{subjectId}|{clientEntityId}", ct);
        var tenantId = db.TenantId;
        await db.LockSubjectRowSharedAsync(subjectId, ct);
        await db.LockMembershipAsync(tenantId, subjectId, ct);
        return await db.TenantMembers.AnyAsync(m => m.TenantId == tenantId && m.SubjectId == subjectId, ct);
    }

    /// <inheritdoc/>
    public virtual async Task<OAuthGrantRecord?> MergeConsentAsync(
        Guid grantId, IEnumerable<string> scopes, bool limitTo24Hours, bool approvedByDeviceCode, CancellationToken ct)
    {
        var db = OwnUnit("A grant is held against its revocation until the caller's unit of work commits.").Context;
        var locks = db.Database.IsNpgsql();
        if (locks && db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("A grant is held against its revocation inside a transaction");

        // A revocation that commits first leaves no row here, and one that follows waits for this
        // unit's commit, so the merge never lands on a revoked grant.
        if (locks
            && !await db.Database
                .SqlQuery<int>($"SELECT 1 AS \"Value\" FROM oauth_grants WHERE id = {grantId} AND revoked_at IS NULL FOR NO KEY UPDATE")
                .AnyAsync(ct))
            return null;

        // The unit tracks what an earlier read returned, and a tracked row keeps the values it was
        // read with: reload it, so the union is over the scopes the held row has now.
        if (db.OAuthGrants.Local.FirstOrDefault(g => g.Id == grantId) is { } tracked)
            await db.Entry(tracked).ReloadAsync(ct);

        var entity = await db.OAuthGrants.Include(g => g.Client).FirstOrDefaultAsync(g => g.Id == grantId, ct);
        if (entity is null || entity.RevokedAt is not null)
            return null;

        Apply(entity, new OAuthGrantEdit(
            null, entity.Scopes.Union(scopes).OrderBy(s => s).ToList(), limitTo24Hours, approvedByDeviceCode));
        return ToRecord(entity);
    }

    private DbUnitOfWork OwnUnit(string reason) =>
        _unit ?? throw new InvalidOperationException(reason);

    private static void Apply(OAuthGrantEntity entity, OAuthGrantEdit edit)
    {
        if (edit.Label is not null)
            entity.Label = edit.Label;
        if (edit.Scopes is not null)
            entity.Scopes = edit.Scopes.ToList();
        if (edit.LimitTo24Hours is { } limit)
            entity.LimitTo24Hours = limit;
        entity.ApprovedByDeviceCode |= edit.ApprovedByDeviceCode;
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
    /// The grants of <paramref name="tenantId"/>, which <paramref name="db"/> is pinned to. The grant's
    /// own tenant is matched as well as filtered, for the reason on
    /// <see cref="IOAuthGrantRepository.FindLiveDirectGrantAsync(Guid, DirectGrantLookup, DateTime, CancellationToken)"/>:
    /// a context pinned to another tenant then finds nothing.
    /// </summary>
    private static IQueryable<OAuthGrantEntity> PinnedGrants(NocturneDbContext db, Guid tenantId) =>
        db.OAuthGrants.AsNoTracking().Where(g => g.TenantId == tenantId);

    /// <summary>
    /// What makes a direct grant itself usable at <paramref name="now"/>, apart from its subject.
    /// Every read of live direct grants goes through it, and
    /// <see cref="OAuthGrantRecord.IsLiveDirectGrantAt"/> is the same rule for a record already read.
    /// </summary>
    /// <remarks>
    /// A grant also authenticates only while its subject is active, a test each authenticating read
    /// adds itself: the pinned read joins it, and the V2 exchange reads the subject. It stays out of
    /// this rule because a record carries no subject state, and because the "secret already in use"
    /// read must still see an inactive subject's grant, or a second grant would take the same secret.
    /// </remarks>
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
    /// <c>RequestMembership</c> does for a subject. A membership that does not meet
    /// <paramref name="inForce"/> joins as none.
    /// </summary>
    private static IQueryable<AuthenticatedRow> WithMembership(
        IQueryable<OAuthGrantEntity> grants, NocturneDbContext db, Guid tenantId, DateTime now,
        Expression<Func<TenantMemberEntity, bool>> inForce) =>
        grants
            .GroupJoin(
                db.TenantMembers.AsNoTracking().Where(inForce).Where(m => m.TenantId == tenantId),
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
                    MemberAccessEndsAt = m.AccessEndsAt,
                    MemberNextReasonLapseAt = m.NextReasonLapseAt,
                    MemberLastUsedAt = m.LastUsedAt,
                    MemberReasons = m.Reasons.AsQueryable()
                        .Where(r => r.TenantMember.NextReasonLapseAt == null || r.TenantMember.NextReasonLapseAt > now)
                        .Select(MembershipReasonColumns.Of).ToList(),
                });

    /// <summary>A grant with the membership columns of the left join; <see cref="MemberId"/> is null when there is none.</summary>
    private sealed class AuthenticatedRow
    {
        public required DirectGrantCredential Grant { get; init; }

        public Guid? MemberId { get; init; }

        public List<string>? DirectPermissions { get; init; }

        public List<List<string>> RolePermissions { get; init; } = [];

        public bool? MemberLimitTo24Hours { get; init; }

        public DateTime? MemberAccessEndsAt { get; init; }

        public DateTime? MemberNextReasonLapseAt { get; init; }

        public DateTime? MemberLastUsedAt { get; init; }

        public List<MembershipReasonColumns> MemberReasons { get; init; } = [];

        public DirectGrantMembership? Membership(DateTime now)
        {
            if (MemberId is not { } id)
                return null;

            var access = new TenantMemberAccess(
                MemberPermissions.Effective(RolePermissions, DirectPermissions).ToHashSet(),
                MemberLimitTo24Hours == true,
                MemberAccessEndsAt);
            return new DirectGrantMembership(
                id, access, MembershipReasonColumns.DirectAccess(MemberReasons, access, now), MemberLastUsedAt);
        }
    }

    private static IReadOnlyList<OAuthGrantRecord> ToRecords(List<OAuthGrantEntity> grants) =>
        grants.Select(g => ToRecord(g)!).ToList();

    private static OAuthGrantRecord? ToRecord(OAuthGrantEntity? grant) =>
        grant is null ? null : new OAuthGrantRecord(
            grant.Id,
            grant.TenantId,
            grant.ClientEntityId,
            grant.Client is null ? null : new OAuthGrantClient(
                grant.Client.ClientId, grant.Client.DisplayName, grant.Client.ClientUri, grant.Client.LogoUri,
                grant.Client.SoftwareId),
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

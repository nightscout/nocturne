using System.Data;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// <see cref="ISubjectRepository"/> over <see cref="NocturneDbContext"/>: one context per call,
/// saving each write, or the caller's <see cref="DbUnitOfWork"/> when constructed over one.
/// </summary>
/// <remarks>
/// Registered transient, like the factory it holds: the authentication middleware, the hub
/// revalidation and the demo refusal resolve it from whatever provider they hold, and a
/// root-resolved repository reads as no one. Writes are tracked reads and saves rather than bulk
/// statements, so a consumer's test on the InMemory provider runs them; the two exceptions, the
/// factor removal's delete inside its serializable transaction and the audit scrub inside the
/// subject delete's, say why.
/// </remarks>
public class SubjectRepository : ISubjectRepository
{
    private readonly INocturneDbContextFactory? _contexts;
    private readonly DbUnitOfWork? _unit;
    private readonly TimeProvider _time;

    /// <summary>A repository whose calls each open their own context and save what they write.</summary>
    public SubjectRepository(INocturneDbContextFactory contexts, TimeProvider? time = null)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// A repository whose calls run on <paramref name="unit"/>, which the caller owns. A write is
    /// staged on the unit and not saved: the owner saves it, so a provisioned owner lands in one
    /// transaction with its tenant and membership. A read tracks its rows, so a write on the same
    /// unit edits the rows it read in place.
    /// </summary>
    public SubjectRepository(DbUnitOfWork unit, TimeProvider? time = null)
    {
        _unit = unit ?? throw new ArgumentNullException(nameof(unit));
        _time = time ?? TimeProvider.System;
    }

    private ValueTask<DbContextLease> OpenAsync(CancellationToken ct) => DbContextLease.OpenAsync(_unit, _contexts, ct);

    private INocturneDbContextFactory Pinnable => _contexts ?? throw new InvalidOperationException(
        "A repository over a unit of work runs on the unit's context and cannot pin another.");

    /// <summary>The factory for a method that commits a transaction of its own, which a unit's owner does instead.</summary>
    private INocturneDbContextFactory Transactional => _contexts ?? throw new InvalidOperationException(
        "A repository over a unit of work runs inside the unit's commit and cannot open a transaction of its own.");

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private Task SaveAsync(NocturneDbContext db, CancellationToken ct) =>
        _unit is null ? db.SaveChangesAsync(ct) : Task.CompletedTask;

    private IQueryable<SubjectEntity> Subjects(NocturneDbContext db) =>
        _unit is null ? db.Subjects.AsNoTracking() : db.Subjects;

    private IQueryable<SubjectEntity> SubjectsWithRoles(NocturneDbContext db) =>
        Subjects(db).Include(s => s.SubjectRoles).ThenInclude(sr => sr.Role);

    /// <inheritdoc/>
    public virtual async Task<Guid> CreateAsync(NewSubject subject, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;
        var entity = new SubjectEntity
        {
            Id = subject.Id ?? Guid.CreateVersion7(),
            Name = subject.Name,
            Username = subject.Username,
            Email = subject.Email,
            Notes = subject.Notes,
            IsActive = subject.IsActive,
            IsSystemSubject = subject.IsSystemSubject,
            IsDemoSubject = subject.IsDemoSubject,
            ApprovalStatus = subject.ApprovalStatus,
            AccessRequestMessage = subject.AccessRequestMessage,
        };
        db.Subjects.Add(entity);

        if (subject.RoleNames is { Count: > 0 } names)
        {
            var wanted = names.Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var roles = await db.Roles.Where(r => wanted.Contains(r.Name)).ToListAsync(ct);
            foreach (var role in roles.DistinctBy(r => r.Id))
            {
                db.SubjectRoles.Add(new SubjectRoleEntity
                {
                    SubjectId = entity.Id,
                    RoleId = role.Id,
                    AssignedAt = Now,
                });
            }
        }

        if (subject.OidcIdentity is { } link)
        {
            db.SubjectOidcIdentities.Add(new SubjectOidcIdentityEntity
            {
                Id = Guid.CreateVersion7(),
                SubjectId = entity.Id,
                ProviderId = link.ProviderId,
                OidcSubjectId = link.OidcSubjectId,
                Issuer = link.Issuer,
                Email = link.Email,
                LinkedAt = link.LinkedAt,
            });
        }

        await SaveAsync(db, ct);
        return entity.Id;
    }

    /// <inheritdoc/>
    public virtual async Task<SubjectRecord?> GetAsync(Guid subjectId, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        return ToRecord(await SubjectsWithRoles(lease.Context).FirstOrDefaultAsync(s => s.Id == subjectId, ct));
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<SubjectRecord>> GetManyAsync(IReadOnlyCollection<Guid> subjectIds, CancellationToken ct)
    {
        if (subjectIds.Count == 0)
            return [];

        await using var lease = await OpenAsync(ct);
        var ids = subjectIds.ToList();
        var entities = await Subjects(lease.Context).Where(s => ids.Contains(s.Id)).ToListAsync(ct);
        return entities.Select(e => ToRecord(e)!).ToList();
    }

    /// <inheritdoc/>
    public virtual async Task<SubjectRecord?> FindSystemSubjectAsync(string name, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        return ToRecord(await SubjectsWithRoles(lease.Context)
            .FirstOrDefaultAsync(s => s.IsSystemSubject && s.Name == name, ct));
    }

    /// <inheritdoc/>
    public virtual async Task<SubjectRecord?> FindMemberByUsernameAsync(
        Guid tenantId, string username, bool activeOnly, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;
        var members = SubjectsWithRoles(db)
            .Where(s => s.Username == username
                && db.TenantMembers.Any(m => m.TenantId == tenantId && m.SubjectId == s.Id));
        if (activeOnly)
            members = members.Where(s => s.IsActive);

        return ToRecord(await members.FirstOrDefaultAsync(ct));
    }

    /// <inheritdoc/>
    public virtual async Task<SubjectRecord?> FindSetupOwnerCandidateAsync(Guid tenantId, CancellationToken ct)
    {
        await using var db = await Pinnable.CreateForTenantAsync(tenantId, ct);
        return ToRecord(await db.Subjects.AsNoTracking()
            .ExcludeDemo()
            .Where(s => !s.IsSystemSubject && s.IsActive)
            .OrderBy(s => s.Id)
            .FirstOrDefaultAsync(ct));
    }

    /// <inheritdoc/>
    public virtual async Task<Guid?> FindDeviceSubjectAsync(CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        return await lease.Context.FindDeviceSubjectOf(lease.Context.TenantId, ct);
    }

    /// <inheritdoc/>
    public virtual async Task<Guid> EnsureDeviceSubjectAsync(CancellationToken ct)
    {
        await using var db = await Transactional.CreateAsync(ct);
        return await db.DeviceSubjectOf(db.TenantId, ct);
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<SubjectRecord>> ListPendingAccessRequestsAsync(CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var entities = await Subjects(lease.Context)
            .Where(s => s.ApprovalStatus == "Pending" && !s.IsSystemSubject)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(ct);
        return entities.Select(e => ToRecord(e)!).ToList();
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<Guid>> FindCredentiallessSubjectIdsAsync(EnrolmentMatch match, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;
        var candidates = match switch
        {
            EnrolmentMatch.Invite { Username: var username } => db.Subjects.Where(
                s => s.Username == username && s.IsActive && s.ApprovalStatus != "Pending"),
            EnrolmentMatch.AccessRequest { DisplayName: var displayName } => db.Subjects.Where(
                s => s.Name == displayName && !s.IsActive && s.ApprovalStatus == "Pending"),
            _ => throw new ArgumentOutOfRangeException(nameof(match)),
        };

        return await candidates
            .Where(s => !s.IsSystemSubject
                && !db.PasskeyCredentials.Any(c => c.SubjectId == s.Id)
                && !db.WorkingOidcIdentities().Any(o => o.SubjectId == s.Id))
            .OrderByDescending(s => s.Id)
            .Select(s => s.Id)
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public virtual async Task<SubjectChange> UpdateAsync(Guid subjectId, SubjectEdit edit, CancellationToken ct)
    {
        if (edit.IsActive is not false)
        {
            await using var lease = await OpenAsync(ct);
            var change = await ApplyEditAsync(lease.Context, subjectId, edit, ct);
            if (change is SubjectChange.Changed)
                await SaveAsync(lease.Context, ct);
            return change;
        }

        if (_unit is not null)
            return await ApplyEditAsync(_unit.Context, subjectId, edit, ct);

        await using var db = await Transactional.CreateAsync(ct);
        return await db.ExecuteInTransactionAsync(
            async token =>
            {
                var change = await ApplyEditAsync(db, subjectId, edit, token);
                if (change is SubjectChange.Changed)
                    await db.SaveChangesAsync(token);
                return change;
            },
            ct: ct);
    }

    /// <summary>
    /// Stages <paramref name="edit"/> on the subject. Deactivating an active subject takes the owner
    /// lock of each tenant it belongs to first (<see cref="TenantOwnerLock"/>), so it runs inside a
    /// transaction.
    /// </summary>
    private static async Task<SubjectChange> ApplyEditAsync(
        NocturneDbContext db, Guid subjectId, SubjectEdit edit, CancellationToken ct)
    {
        var entity = await db.Subjects.FirstOrDefaultAsync(s => s.Id == subjectId, ct);
        if (entity is null)
            return new SubjectChange.NotFound();

        if (edit.IsActive is false && entity.IsActive)
        {
            var lastOwnerOf = await db.LockTenantsOfSubjectAsync(subjectId, ct);
            if (lastOwnerOf.Count > 0)
                return new SubjectChange.LastOwnerOf(lastOwnerOf);
        }

        if (edit.Name is not null)
            entity.Name = edit.Name;
        if (edit.Username is not null)
            entity.Username = edit.Username;
        if (edit.Email is not null)
            entity.Email = edit.Email;
        if (edit.IsActive is { } active)
            entity.IsActive = active;
        if (edit.IsPlatformAdmin is { } platformAdmin)
            entity.IsPlatformAdmin = platformAdmin;
        if (edit.ApprovalStatus is not null)
            entity.ApprovalStatus = edit.ApprovalStatus;
        if (edit.PreferredLanguage is not null)
            entity.PreferredLanguage = edit.PreferredLanguage;
        if (edit.Preferences is not null)
            entity.Preferences = edit.Preferences;
        if (edit.LastLoginAt is { } lastLogin)
            entity.LastLoginAt = lastLogin;

        return new SubjectChange.Changed();
    }

    /// <inheritdoc/>
    public virtual async Task<SubjectRecord?> UpdateProfileAsync(Guid subjectId, SubjectProfile profile, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;
        var entity = await db.Subjects
            .Include(s => s.SubjectRoles).ThenInclude(sr => sr.Role)
            .FirstOrDefaultAsync(s => s.Id == subjectId, ct);
        if (entity is null)
            return null;

        entity.Name = profile.Name;
        entity.Email = profile.Email;
        entity.Notes = profile.Notes;

        await SaveAsync(db, ct);
        return ToRecord(entity);
    }

    /// <inheritdoc/>
    public virtual async Task<bool> SetAccessRequestMessageAsync(Guid subjectId, string? message, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;
        var entity = await db.Subjects.FirstOrDefaultAsync(s => s.Id == subjectId, ct);
        if (entity is null)
            return false;

        entity.AccessRequestMessage = message;
        await SaveAsync(db, ct);
        return true;
    }

    /// <inheritdoc/>
    public virtual async Task<SubjectChange> DeleteAsync(Guid subjectId, CancellationToken ct)
    {
        await using var db = await Transactional.CreateAsync(ct);
        var entity = await db.Subjects.FirstOrDefaultAsync(s => s.Id == subjectId, ct);
        if (entity is null)
            return new SubjectChange.NotFound();

        return await db.Database.CreateExecutionStrategy().ExecuteAsync<SubjectChange>(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            if (entity.IsActive)
            {
                var lastOwnerOf = await db.LockTenantsOfSubjectAsync(subjectId, ct);
                if (lastOwnerOf.Count > 0)
                    return new SubjectChange.LastOwnerOf(lastOwnerOf);
            }

            // One statement per column set: the audit trail of a long-lived subject is every
            // sign-in it ever made, which is not loaded into the tracker to null two columns.
            await db.AuthAuditLog
                .Where(a => a.ActorSubjectId == subjectId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.IpAddress, (string?)null)
                    .SetProperty(a => a.UserAgent, (string?)null), ct);
            await db.AuthAuditLog
                .Where(a => a.TargetSubjectId == subjectId || a.ActorSubjectId == subjectId)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.DetailsJson, (string?)null), ct);

            db.Subjects.Remove(entity);
            await db.SaveChangesAsync(ct);

            await ScrubTenantAuditTrailsAsync(db, subjectId, ct);

            await tx.CommitAsync(ct);
            return new SubjectChange.Changed();
        });
    }

    /// <summary>
    /// Clears the subject's client address and user agent from the audit trail of every tenant, not
    /// only those it still belongs to: a member removed earlier, or a platform admin who never
    /// joined, has rows there too.
    /// </summary>
    /// <remarks>
    /// Both tables force row level security, so each tenant's statements run with its id set local
    /// to the transaction, which the commit discards. The tenant query filter reads the context's
    /// tenant, so it is lifted and the tenant named in the predicate. Runs after the subject delete,
    /// so the last tenant set governs no other statement.
    /// </remarks>
    private static async Task ScrubTenantAuditTrailsAsync(NocturneDbContext db, Guid subjectId, CancellationToken ct)
    {
        var tenantIds = await db.Tenants.AsNoTracking().Select(t => t.Id).ToListAsync(ct);
        foreach (var tenantId in tenantIds)
        {
            if (db.Database.IsNpgsql())
            {
                await db.Database.ExecuteSqlRawAsync(
                    "SELECT set_config('app.current_tenant_id', {0}, true)", [tenantId.ToString()], ct);
            }

            await db.MutationAuditLog
                .IgnoreQueryFilters([NocturneDbContext.TenantFilterKey])
                .Where(a => a.TenantId == tenantId && a.ActorSubjectId == subjectId)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.IpAddress, (string?)null), ct);
            await db.ReadAccessLog
                .IgnoreQueryFilters([NocturneDbContext.TenantFilterKey])
                .Where(a => a.TenantId == tenantId && a.ActorSubjectId == subjectId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.IpAddress, (string?)null)
                    .SetProperty(a => a.UserAgent, (string?)null), ct);
        }
    }

    /// <inheritdoc/>
    public virtual async Task<bool?> IsDemoSubjectAsync(Guid subjectId, CancellationToken ct)
    {
        await using var db = await Pinnable.CreatePlatformAsync(ct);
        return await db.Subjects.AsNoTracking()
            .Where(s => s.Id == subjectId)
            .Select(s => (bool?)s.IsDemoSubject)
            .FirstOrDefaultAsync(ct);
    }

    /// <inheritdoc/>
    public virtual async Task<bool> IsPlatformAdminAsync(Guid subjectId, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        return await lease.Context.Subjects.AsNoTracking()
            .Where(s => s.Id == subjectId)
            .Select(s => s.IsPlatformAdmin)
            .FirstOrDefaultAsync(ct);
    }

    /// <inheritdoc/>
    public virtual async Task<bool> AnyPlatformAdminAsync(CancellationToken ct)
    {
        await using var db = await Pinnable.CreatePlatformAsync(ct);
        return await db.Subjects.AnyAsync(s => s.IsPlatformAdmin, ct);
    }

    /// <inheritdoc/>
    public virtual async Task<int> CountPlatformAdminsAsync(CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        return await lease.Context.Subjects.CountAsync(s => s.IsPlatformAdmin, ct);
    }

    /// <inheritdoc/>
    public virtual async Task<int> GrantPlatformAdminAsync(IReadOnlyCollection<Guid> subjectIds, CancellationToken ct)
    {
        if (subjectIds.Count == 0)
            return 0;

        await using var db = await Pinnable.CreatePlatformAsync(ct);
        var ids = subjectIds.ToList();
        var subjects = await db.Subjects.Where(s => ids.Contains(s.Id)).ToListAsync(ct);
        foreach (var subject in subjects)
            subject.IsPlatformAdmin = true;

        await db.SaveChangesAsync(ct);
        return subjects.Count;
    }

    /// <inheritdoc/>
    public virtual async Task<SubjectSignInState?> GetSignInStateAsync(Guid subjectId, CancellationToken ct)
    {
        await using var db = await Pinnable.CreateForSubjectAsync(subjectId, ct);
        return await db.Subjects.AsNoTracking()
            .Where(s => s.Id == subjectId)
            .Select(s => new SubjectSignInState(s.IsActive, s.IsPlatformAdmin))
            .FirstOrDefaultAsync(ct);
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<Role>> GetRolesAsync(Guid subjectId, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var roles = await lease.Context.SubjectRoles.AsNoTracking()
            .Where(sr => sr.SubjectId == subjectId)
            .Select(sr => sr.Role!)
            .ToListAsync(ct);
        return roles.Select(ToRole).ToList();
    }

    /// <inheritdoc/>
    public virtual async Task<RoleAssignment> AssignRoleAsync(
        Guid subjectId, string roleName, Guid? assignedBy, DateTime now, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;
        var role = await db.Roles.AsNoTracking().FirstOrDefaultAsync(r => r.Name == roleName, ct);
        if (role is null)
            return RoleAssignment.RoleNotFound;

        if (await db.SubjectRoles.AnyAsync(sr => sr.SubjectId == subjectId && sr.RoleId == role.Id, ct))
            return RoleAssignment.AlreadyAssigned;

        db.SubjectRoles.Add(new SubjectRoleEntity
        {
            SubjectId = subjectId,
            RoleId = role.Id,
            AssignedAt = now,
            AssignedById = assignedBy,
        });
        await SaveAsync(db, ct);
        return RoleAssignment.Assigned;
    }

    /// <inheritdoc/>
    public virtual async Task<bool> RemoveRoleAsync(Guid subjectId, string roleName, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;
        var assignments = await db.SubjectRoles
            .Where(sr => sr.SubjectId == subjectId && sr.Role!.Name == roleName)
            .ToListAsync(ct);
        if (assignments.Count == 0)
            return false;

        db.SubjectRoles.RemoveRange(assignments);
        await SaveAsync(db, ct);
        return true;
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<SubjectOidcIdentity>> GetOidcIdentitiesAsync(Guid subjectId, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var identities = await lease.Context.SubjectOidcIdentities.AsNoTracking()
            .Include(x => x.Provider)
            .Where(x => x.SubjectId == subjectId)
            .OrderBy(x => x.LinkedAt)
            .ToListAsync(ct);
        return identities.Select(ToIdentity).ToList();
    }

    /// <inheritdoc/>
    public virtual async Task<OidcIdentityMatch?> FindByOidcIdentityAsync(string oidcSubjectId, string issuer, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var identities = _unit is null
            ? lease.Context.SubjectOidcIdentities.AsNoTracking()
            : lease.Context.SubjectOidcIdentities;
        var identity = await identities
            .Include(x => x.Provider)
            .Include(x => x.Subject!).ThenInclude(s => s.SubjectRoles).ThenInclude(sr => sr.Role)
            .FirstOrDefaultAsync(x => x.OidcSubjectId == oidcSubjectId && x.Issuer == issuer, ct);
        return identity?.Subject is null ? null : new OidcIdentityMatch(ToIdentity(identity), ToRecord(identity.Subject)!);
    }

    /// <inheritdoc/>
    public virtual async Task<Guid> AddOidcIdentityAsync(NewSubjectOidcIdentity identity, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;
        var entity = new SubjectOidcIdentityEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = identity.SubjectId,
            ProviderId = identity.ProviderId,
            OidcSubjectId = identity.OidcSubjectId,
            Issuer = identity.Issuer,
            Email = identity.Email,
            LinkedAt = identity.LinkedAt,
        };
        db.SubjectOidcIdentities.Add(entity);
        await SaveAsync(db, ct);
        return entity.Id;
    }

    /// <inheritdoc/>
    public virtual async Task<bool> TouchOidcIdentityAsync(Guid identityId, DateTime now, string? email, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;
        var entity = await db.SubjectOidcIdentities.FirstOrDefaultAsync(x => x.Id == identityId, ct);
        if (entity is null)
            return false;

        entity.LastUsedAt = now;
        if (!string.IsNullOrEmpty(email) && entity.Email != email)
            entity.Email = email;
        await SaveAsync(db, ct);
        return true;
    }

    /// <inheritdoc/>
    public virtual async Task<int> CountPrimaryAuthFactorsAsync(Guid subjectId, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        return await CountFactorsAsync(lease.Context, subjectId, except: null, ct);
    }

    /// <inheritdoc/>
    public virtual async Task<FactorRemovalResult> TryRemoveFactorAsync(Guid subjectId, SignInFactor factor, CancellationToken ct)
    {
        await using var db = await Transactional.CreateAsync(ct);
        return factor switch
        {
            SignInFactor.Passkey { CredentialId: var id } => await RemoveFactorAsync(
                db, subjectId, factor, db.PasskeyCredentials.Where(c => c.Id == id && c.SubjectId == subjectId), ct),
            SignInFactor.OidcIdentity { IdentityId: var id } => await RemoveFactorAsync(
                db, subjectId, factor, db.SubjectOidcIdentities.Where(i => i.Id == id && i.SubjectId == subjectId), ct),
            _ => throw new ArgumentOutOfRangeException(nameof(factor)),
        };
    }

    private static Task<FactorRemovalResult> RemoveFactorAsync<TFactor>(
        NocturneDbContext db, Guid subjectId, SignInFactor factor, IQueryable<TFactor> rows, CancellationToken ct)
        where TFactor : class =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);

            if (!await rows.AnyAsync(ct))
                return FactorRemovalResult.NotFound;

            if (await CountFactorsAsync(db, subjectId, factor, ct) < 1)
                return FactorRemovalResult.LastPrimaryFactor;

            // Untracked, so a retried attempt never inherits a Deleted entry from the failed one.
            await rows.ExecuteDeleteAsync(ct);
            await tx.CommitAsync(ct);
            return FactorRemovalResult.Removed;
        });

    /// <summary>
    /// The subject's primary factors, leaving out <paramref name="except"/> when one is being
    /// removed: passkeys, and OIDC identities whose provider can sign in
    /// (<see cref="WorkingOidcIdentityFilter"/>).
    /// </summary>
    private static async Task<int> CountFactorsAsync(NocturneDbContext db, Guid subjectId, SignInFactor? except, CancellationToken ct)
    {
        var passkeys = db.PasskeyCredentials.Where(p => p.SubjectId == subjectId);
        var identities = db.WorkingOidcIdentities().Where(i => i.SubjectId == subjectId);
        switch (except)
        {
            case SignInFactor.Passkey { CredentialId: var id }:
                passkeys = passkeys.Where(p => p.Id != id);
                break;
            case SignInFactor.OidcIdentity { IdentityId: var id }:
                identities = identities.Where(i => i.Id != id);
                break;
        }

        return await passkeys.CountAsync(ct) + await identities.CountAsync(ct);
    }

    /// <inheritdoc/>
    public virtual async Task<int> CountStrandedByProviderAsync(Guid providerId, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;
        return await db.WorkingOidcIdentities()
            .Where(i => i.ProviderId == providerId)
            .Select(i => i.Subject!)
            .Where(s => s.IsActive
                && !s.IsSystemSubject
                && !s.IsDemoSubject
                && !db.PasskeyCredentials.Any(p => p.SubjectId == s.Id)
                && !db.WorkingOidcIdentities().Any(o => o.SubjectId == s.Id && o.ProviderId != providerId))
            .Select(s => s.Id)
            .Distinct()
            .CountAsync(ct);
    }

    /// <inheritdoc/>
    public virtual async Task<bool> TenantHasCredentialedMemberAsync(Guid tenantId, CancellationToken ct)
    {
        await using var db = await Pinnable.CreateSystemPinnedAsync(tenantId, ct);
        return await db.TenantMembers.AsNoTracking()
            .Where(m => m.TenantId == tenantId)
            .AnyAsync(m =>
                db.PasskeyCredentials.Any(c => c.SubjectId == m.SubjectId) ||
                db.WorkingOidcIdentities().Any(o => o.SubjectId == m.SubjectId), ct);
    }

    /// <inheritdoc/>
    public virtual async Task<bool> HasOrphanedMemberAsync(Guid tenantId, CancellationToken ct)
    {
        await using var db = await Pinnable.CreateSystemPinnedAsync(tenantId, ct);
        return await db.OrphanedSubjectsOf(tenantId).AnyAsync(ct);
    }

    /// <inheritdoc/>
    public virtual async Task<DateTime?> LastModifiedAsync(CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        return await lease.Context.Subjects.AsNoTracking()
            .OrderByDescending(s => s.UpdatedAt)
            .Select(s => (DateTime?)s.UpdatedAt)
            .FirstOrDefaultAsync(ct);
    }

    private static SubjectRecord? ToRecord(SubjectEntity? subject) =>
        subject is null ? null : new SubjectRecord(
            subject.Id,
            subject.Name,
            subject.Username,
            subject.Email,
            subject.Notes,
            subject.IsActive,
            subject.IsSystemSubject,
            subject.IsDemoSubject,
            subject.IsPlatformAdmin,
            subject.ApprovalStatus,
            subject.AccessRequestMessage,
            subject.PreferredLanguage,
            subject.Preferences,
            subject.AvatarUrl,
            subject.CreatedAt,
            subject.UpdatedAt,
            subject.LastLoginAt,
            subject.SubjectRoles.Where(sr => sr.Role is not null).Select(sr => ToRole(sr.Role!)).ToList());

    private static Role ToRole(RoleEntity role) => new()
    {
        Id = role.Id,
        Name = role.Name,
        Description = role.Description,
        Permissions = role.Permissions is null ? [] : [.. role.Permissions],
        IsSystemRole = role.IsSystemRole,
        CreatedAt = role.CreatedAt,
        UpdatedAt = role.UpdatedAt,
    };

    private static SubjectOidcIdentity ToIdentity(SubjectOidcIdentityEntity identity) => new()
    {
        Id = identity.Id,
        SubjectId = identity.SubjectId,
        ProviderId = identity.ProviderId,
        ProviderName = identity.Provider?.Name ?? "Unknown",
        ProviderIcon = identity.Provider?.Icon,
        ProviderButtonColor = identity.Provider?.ButtonColor,
        OidcSubjectId = identity.OidcSubjectId,
        Issuer = identity.Issuer,
        Email = identity.Email,
        LinkedAt = identity.LinkedAt,
        LastUsedAt = identity.LastUsedAt,
    };
}

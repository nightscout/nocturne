using System.Data;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Mappers;
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
    private readonly IAccountLinkRoute _linkRoute;

    /// <summary>A repository whose calls each open their own context and save what they write.</summary>
    /// <param name="contexts">Opens the context of each call.</param>
    /// <param name="linkRoute">Decides who cannot sign in (<see cref="OrphanedSubjectFilter"/>).</param>
    /// <param name="time">The clock; the system clock when null.</param>
    public SubjectRepository(INocturneDbContextFactory contexts, IAccountLinkRoute linkRoute, TimeProvider? time = null)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        _linkRoute = linkRoute ?? throw new ArgumentNullException(nameof(linkRoute));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// A repository whose calls run on <paramref name="unit"/>, which the caller owns. A write is
    /// staged on the unit and not saved: the owner saves it, so a provisioned owner lands in one
    /// transaction with its tenant and membership. A read tracks its rows, so a write on the same
    /// unit edits the rows it read in place.
    /// </summary>
    /// <param name="unit">The caller's unit of work.</param>
    /// <param name="linkRoute">Decides who cannot sign in (<see cref="OrphanedSubjectFilter"/>).</param>
    /// <param name="time">The clock; the system clock when null.</param>
    public SubjectRepository(DbUnitOfWork unit, IAccountLinkRoute linkRoute, TimeProvider? time = null)
    {
        _unit = unit ?? throw new ArgumentNullException(nameof(unit));
        _linkRoute = linkRoute ?? throw new ArgumentNullException(nameof(linkRoute));
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
        var id = await StageSubjectAsync(db, subject, ct);
        await SaveAsync(db, ct);
        return id;
    }

    /// <inheritdoc/>
    public virtual async Task<Guid> CreateSetupOwnerAsync(Guid tenantId, NewSubject subject, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;
        var tenant = await db.Tenants.FirstAsync(t => t.Id == tenantId, ct);
        var id = await StageSubjectAsync(db, subject, ct);
        tenant.SetupOwnerSubjectId = id;
        await SaveAsync(db, ct);
        return id;
    }

    /// <inheritdoc/>
    public virtual async Task ClearSetupOwnerAsync(Guid subjectId, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;
        var tenants = await db.Tenants.Where(t => t.SetupOwnerSubjectId == subjectId).ToListAsync(ct);
        foreach (var tenant in tenants)
            tenant.SetupOwnerSubjectId = null;
        await SaveAsync(db, ct);
    }

    private async Task<Guid> StageSubjectAsync(NocturneDbContext db, NewSubject subject, CancellationToken ct)
    {
        var entity = new SubjectEntity
        {
            Id = subject.Id ?? Guid.CreateVersion7(),
            Name = subject.Name,
            Username = subject.Username,
            Notes = subject.Notes,
            IsActive = subject.IsActive,
            IsSystemSubject = subject.IsSystemSubject,
            IsDemoSubject = subject.IsDemoSubject,
            ApprovalStatus = subject.ApprovalStatus,
            AccessRequestMessage = subject.AccessRequestMessage,
        };
        entity.SetEmail(subject.Email);
        if (subject.EmailConfirmedAt is { } confirmedAt)
            entity.ConfirmEmail(subject.Email, NocturneDbContext.AtStoredPrecision(confirmedAt));
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
        Guid tenantId, string username, bool activeOnly, DateTime now, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;
        var live = db.TenantMembers.LiveAt(now);
        var members = SubjectsWithRoles(db)
            .Where(s => s.Username == username
                && live.Any(m => m.TenantId == tenantId && m.SubjectId == s.Id));
        if (activeOnly)
            members = members.Where(s => s.IsActive);

        return ToRecord(await members.FirstOrDefaultAsync(ct));
    }

    /// <inheritdoc/>
    public virtual async Task<IReadOnlyList<SubjectRecord>> FindMembersByConfirmedEmailAsync(
        Guid tenantId, string email, DateTime now, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;
        var live = db.TenantMembers.LiveAt(now);
        // The query only narrows to the tenant's confirmed members; whether an address is the one
        // named is decided by EmailAddresses.AreSame below, so no SQL comparison restates it.
        var confirmed = await Subjects(db)
            .ExcludeDemo()
            .Where(OrphanedSubjectFilter.HoldsConfirmedEmail)
            .Where(s => s.IsActive && !s.IsSystemSubject
                && live.Any(m => m.TenantId == tenantId && m.SubjectId == s.Id))
            .ToListAsync(ct);
        return confirmed
            .Where(s => EmailAddresses.AreSame(s.Email, email))
            .Select(s => ToRecord(s)!)
            .ToList();
    }

    /// <inheritdoc/>
    public virtual async Task<SubjectRecord?> FindSetupOwnerCandidateAsync(Guid tenantId, CancellationToken ct)
    {
        await using var db = await Pinnable.CreateForTenantAsync(tenantId, ct);
        var now = Now;
        var candidates = db.Subjects.AsNoTracking()
            .ExcludeDemo()
            .Where(s => db.Tenants.Any(t => t.Id == tenantId && t.SetupOwnerSubjectId == s.Id)
                && !s.IsSystemSubject && s.IsActive
                && !db.PasskeyCredentials.Any(c => c.SubjectId == s.Id)
                && !db.SubjectOidcIdentities.Any(o => o.SubjectId == s.Id)
                && !db.TotpCredentials.Any(t => t.SubjectId == s.Id)
                && !db.RefreshTokens.Any(t => t.SubjectId == s.Id && t.ExpiresAt > now)
                && !db.LoginCodes.Any(c => c.SubjectId == s.Id && c.ConsumedAt == null && c.ExpiresAt > now
                    && SetupOwnerRefusedCodePurposes.Contains(c.Purpose)));
        if (_linkRoute.IsConfigured)
            candidates = candidates.Where(OrphanedSubjectFilter.LacksConfirmedEmail);
        return ToRecord(await candidates.FirstOrDefaultAsync(ct));
    }

    /// <summary>
    /// The login code purposes that let their holder into the account: a recovery code an
    /// administrator or the operator issued, an emailed recovery link, the recovery session one of
    /// those starts, and a login code handed to another browser.
    /// </summary>
    private static readonly LoginCodePurpose[] SetupOwnerRefusedCodePurposes =
    [
        LoginCodePurpose.Recovery, LoginCodePurpose.OperatorRecovery,
        LoginCodePurpose.EmailRecovery, LoginCodePurpose.RecoverySession, LoginCodePurpose.Login,
    ];

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
        var db = lease.Context;
        var entities = await PendingAccessRequests(db, Subjects(db))
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(ct);
        return entities.Select(e => ToRecord(e)!).ToList();
    }

    /// <inheritdoc/>
    public virtual async Task<bool> AnyPendingAccessRequestNamedAsync(Guid tenantId, string name, CancellationToken ct)
    {
        await using var db = await Pinnable.CreateSystemPinnedAsync(tenantId, ct);
        return await PendingAccessRequests(db, db.Subjects.AsNoTracking())
            .AnyAsync(s => s.Name == name
                && db.MembershipRequests.Any(r => r.TenantId == tenantId && r.SubjectId == s.Id
                    && r.Status == MembershipRequestRecord.Pending), ct);
    }

    private static IQueryable<SubjectEntity> PendingAccessRequests(NocturneDbContext db, IQueryable<SubjectEntity> subjects) =>
        subjects.Where(s => s.ApprovalStatus == nameof(ApprovalStatus.Pending) && !s.IsSystemSubject
            && db.PasskeyCredentials.Any(c => c.SubjectId == s.Id));

    /// <inheritdoc/>
    public virtual async Task<bool> IsEnrollingSubjectAsync(Guid subjectId, EnrolmentMatch match, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;
        var subject = db.Subjects.Where(s => s.Id == subjectId);
        var candidates = match switch
        {
            EnrolmentMatch.Invite { Username: var username } => subject.Where(
                s => s.Username == username && s.IsActive && s.ApprovalStatus != nameof(ApprovalStatus.Pending)),
            EnrolmentMatch.AccessRequest { DisplayName: var displayName } => subject.Where(
                s => s.Name == displayName && !s.IsActive && s.ApprovalStatus == nameof(ApprovalStatus.Pending)),
            _ => throw new ArgumentOutOfRangeException(nameof(match)),
        };

        var now = Now;
        return await candidates
            .Where(s => !s.IsSystemSubject
                && !db.PasskeyCredentials.Any(c => c.SubjectId == s.Id)
                && !db.SubjectOidcIdentities.Any(o => o.SubjectId == s.Id)
                && !db.TotpCredentials.Any(t => t.SubjectId == s.Id)
                && !db.RefreshTokens.Any(t => t.SubjectId == s.Id && t.ExpiresAt > now))
            .AnyAsync(ct);
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
    private async Task<SubjectChange> ApplyEditAsync(
        NocturneDbContext db, Guid subjectId, SubjectEdit edit, CancellationToken ct)
    {
        var entity = await db.Subjects.FirstOrDefaultAsync(s => s.Id == subjectId, ct);
        if (entity is null)
            return new SubjectChange.NotFound();

        if (edit.IsActive is false && entity.IsActive)
        {
            var lastOwnerOf = await db.LockTenantsOfSubjectAsync(subjectId, _linkRoute, ct);
            if (lastOwnerOf.Count > 0)
                return new SubjectChange.LastOwnerOf(lastOwnerOf);
        }

        if (edit.Name is not null)
            entity.Name = edit.Name;
        if (edit.Username is not null)
            entity.Username = edit.Username;
        if (edit.Email is not null)
            StageEmail(db, entity, edit.Email, edit.EmailConfirmedAt);
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
        StageEmail(db, entity, profile.Email, confirmedAt: null);
        entity.Notes = profile.Notes;

        await SaveAsync(db, ct);
        return ToRecord(entity);
    }

    /// <summary>
    /// Stages <paramref name="email"/> on the subject, confirmed at <paramref name="confirmedAt"/>
    /// when one is given. A save writes only the columns the tracker saw change, so a changed
    /// address whose confirmation was already null would write the address alone, and a
    /// confirmation that committed in between would then outlive it. Both columns are marked
    /// modified whenever the address changed or a confirmation rides with it, so the row always
    /// takes the pair this edit holds. See <see cref="SubjectEntity.SetEmail"/> for the rule.
    /// </summary>
    private static void StageEmail(NocturneDbContext db, SubjectEntity entity, string? email, DateTime? confirmedAt)
    {
        var changed = entity.SetEmail(email);
        var confirmed = confirmedAt is { } at && entity.ConfirmEmail(email, NocturneDbContext.AtStoredPrecision(at));
        if (!changed && !confirmed)
            return;

        var entry = db.Entry(entity);
        entry.Property(s => s.Email).IsModified = true;
        entry.Property(s => s.EmailConfirmedAt).IsModified = true;
    }

    /// <inheritdoc/>
    public virtual async Task<EmailConfirmation> ConfirmEmailAsync(
        Guid subjectId, string email, DateTime now, CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        var db = lease.Context;
        var at = NocturneDbContext.AtStoredPrecision(now);

        // The rule is decided in .NET on the row just read. The statement is an exact
        // compare-and-set on that string, so it states no comparison of its own and still
        // confirms nothing for an address that changed in between. One more attempt covers a
        // row that changed underneath; a second change is refused.
        for (var attempt = 0; ; attempt++)
        {
            var stored = await db.Subjects.AsNoTracking().FirstOrDefaultAsync(s => s.Id == subjectId, ct);
            if (stored is null)
                return new EmailConfirmation.NotFound();
            if (!EmailAddresses.AreSame(stored.Email, email))
                return new EmailConfirmation.AddressMismatch();
            if (stored.EmailConfirmedAt is { } first)
                return new EmailConfirmation.Confirmed(first);
            if (attempt == 2)
                return new EmailConfirmation.AddressMismatch();

            var storedEmail = stored.Email;
            var updated = await db.Subjects
                .Where(s => s.Id == subjectId && s.EmailConfirmedAt == null && s.Email == storedEmail)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.EmailConfirmedAt, at)
                    .SetProperty(x => x.UpdatedAt, NocturneDbContext.UtcNowAtStoredPrecision()), ct);
            if (updated == 1)
            {
                RefreshTrackedConfirmation(db, subjectId, at);
                return new EmailConfirmation.Confirmed(at);
            }
        }
    }

    /// <summary>
    /// The confirmation statement bypasses the tracker, so a copy a unit of work holds would
    /// otherwise be written back stale. A reload would drop the unit's staged edits, so only the
    /// confirmation is brought up to date, unless the unit stages a pair of its own, which
    /// overwrites it.
    /// </summary>
    private void RefreshTrackedConfirmation(NocturneDbContext db, Guid subjectId, DateTime at)
    {
        if (_unit is null
            || db.ChangeTracker.Entries<SubjectEntity>().FirstOrDefault(e => e.Entity.Id == subjectId) is not { } tracked
            || tracked.Property(s => s.EmailConfirmedAt).IsModified)
            return;

        var confirmation = tracked.Property(s => s.EmailConfirmedAt);
        confirmation.OriginalValue = at;
        confirmation.CurrentValue = at;
        confirmation.IsModified = false;
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
                var lastOwnerOf = await db.LockTenantsOfSubjectAsync(subjectId, _linkRoute, ct);
                if (lastOwnerOf.Count > 0)
                    return new SubjectChange.LastOwnerOf(lastOwnerOf);
            }

            var (soleMemberOf, lastManagerOf) = await db.LockTeamsOfSubjectAsync(subjectId, ct);
            if (lastManagerOf.Count > 0)
                return new SubjectChange.LastManagerOf(lastManagerOf);

            // Removed here rather than by the subject's cascade so each membership leaves its removed
            // row; saved before the scrub below, which then clears that row's details like the rest.
            // The teams are staged after that save, so their rows keep their details.
            var memberships = await db.TenantMembers.Where(m => m.SubjectId == subjectId).ToListAsync(ct);
            foreach (var membership in memberships)
                await MembershipReasons.RemoveMemberAsync(db, membership, MembershipEndHow.AccountDeleted, ct);
            await db.SaveChangesAsync(ct);

            foreach (var team in soleMemberOf)
                await TeamRepository.RemoveAsync(db, team, ct);

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

            await ScrubTenantRowsAsync(db, subjectId, ct);

            await tx.CommitAsync(ct);
            return new SubjectChange.Changed();
        });
    }

    /// <summary>
    /// Clears the subject's client address and user agent from the audit trail of every tenant, not
    /// only those it still belongs to: a member removed earlier, or a platform admin who never
    /// joined, has rows there too. Removes the subject's membership requests there as well, which
    /// carry its own message and would otherwise sit in an owner's list naming no one.
    /// </summary>
    /// <remarks>
    /// Both tables force row level security, so each tenant's statements run with its id set local
    /// to the transaction, which the commit discards. The tenant query filter reads the context's
    /// tenant, so it is lifted and the tenant named in the predicate. Runs after the subject delete,
    /// so the last tenant set governs no other statement.
    /// </remarks>
    private static async Task ScrubTenantRowsAsync(NocturneDbContext db, Guid subjectId, CancellationToken ct)
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
            await db.MembershipRequests
                .IgnoreQueryFilters([NocturneDbContext.TenantFilterKey])
                .Where(r => r.TenantId == tenantId && r.SubjectId == subjectId)
                .ExecuteDeleteAsync(ct);
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
            .Select(s => s.IsPlatformAdmin && s.IsActive)
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
        return roles.Select(RoleMapper.ToDomainModel).ToList();
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
    public virtual async Task<bool> AdoptForSetupAsync(
        Guid createdId, Guid holderId, Guid identityId, string tenantSlug, CancellationToken ct)
    {
        await using var db = await Transactional.CreateAsync(ct);
        if (db.HistoryClamped || db.IsShareContext || createdId == holderId)
            return false;

        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);

            var tenantId = await db.Tenants.AsNoTracking()
                .Where(t => t.Slug == tenantSlug).Select(t => (Guid?)t.Id).FirstOrDefaultAsync(ct);
            var holder = await AdoptableBySetup(db, identityId).FirstOrDefaultAsync(s => s.Id == holderId, ct);
            var memberships = await db.TenantMembers.Include(m => m.MemberRoles)
                .Where(m => m.SubjectId == createdId).Take(2).ToListAsync(ct);
            if (tenantId is null || holder is null || memberships.Count != 1)
                return false;

            var created = await Unclaimed(db, identityId: null, ownerOf: tenantId)
                .FirstOrDefaultAsync(s => s.Id == createdId, ct);
            if (created is null || await NamedByTenantRowsAsync(db, createdId, ct))
                return false;

            var now = Now;
            var moved = memberships[0];
            var held = new NewTenantMember(
                moved.TenantId, holderId, moved.MemberRoles.Select(mr => mr.TenantRoleId).ToList(),
                moved.DirectPermissions?.ToList(), moved.Label, moved.Username, moved.LimitTo24Hours,
                moved.CreatedFromInviteId, MembershipMadeBy.Member, moved.AccessEndsAt)
            {
                How = MembershipJoinHow.Setup,
                By = null,
            };
            await TenantMembershipRepository.StageRemoveAsync(db, moved, MembershipEndHow.System, ct);
            await TenantMembershipRepository.StageAddAsync(db, held, ct);
            holder.Name = created.Name;
            holder.Username = created.Username;
            var heldRoleIds = db.SubjectRoles.Where(r => r.SubjectId == holderId).Select(r => r.RoleId);
            var passedRoles = await db.SubjectRoles.AsNoTracking()
                .Where(r => r.SubjectId == createdId && !heldRoleIds.Contains(r.RoleId))
                .ToListAsync(ct);
            foreach (var role in passedRoles)
            {
                db.SubjectRoles.Add(new SubjectRoleEntity
                {
                    SubjectId = holderId,
                    RoleId = role.RoleId,
                    AssignedAt = now,
                    AssignedById = role.AssignedById,
                });
            }
            await EfFirstPartyTokenRepository.StageRevokeAllForSubjectAsync(
                db, holderId, "Adopted as the setup owner", ct);
            await db.SaveChangesAsync(ct);

            db.Subjects.Remove(created);
            await db.SaveChangesAsync(ct);

            await tx.CommitAsync(ct);
            return true;
        });
    }

    /// <summary>The subjects <see cref="ISubjectRepository.AdoptForSetupAsync"/> may adopt for <paramref name="identityId"/>.</summary>
    private static IQueryable<SubjectEntity> AdoptableBySetup(NocturneDbContext db, Guid identityId) =>
        db.Subjects.Where(s => s.IsActive && !s.IsSystemSubject && !s.IsDemoSubject && !s.IsPlatformAdmin
            && !db.TenantMembers.Any(m => m.SubjectId == s.Id)
            && !db.PasskeyCredentials.Any(c => c.SubjectId == s.Id)
            && !db.TotpCredentials.Any(t => t.SubjectId == s.Id)
            && db.SubjectOidcIdentities.Any(o => o.SubjectId == s.Id && o.Id == identityId)
            && !db.SubjectOidcIdentities.Any(o => o.SubjectId == s.Id && o.Id != identityId));

    /// <inheritdoc/>
    public virtual async Task<Guid?> TakeUnclaimedOidcIdentityAsync(Guid identityId, Guid subjectId, string? email, CancellationToken ct)
    {
        await using var db = await Transactional.CreateAsync(ct);
        if (db.HistoryClamped || db.IsShareContext)
            return null;

        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);

            var identity = await db.SubjectOidcIdentities.AsNoTracking()
                .FirstOrDefaultAsync(o => o.Id == identityId && o.SubjectId != subjectId, ct);
            if (identity is null)
                return (Guid?)null;

            var holder = await Unclaimed(db, identityId, ownerOf: null)
                .FirstOrDefaultAsync(s => s.Id == identity.SubjectId, ct);
            if (holder is null || await NamedByTenantRowsAsync(db, holder.Id, ct))
                return null;

            db.Subjects.Remove(holder);
            await db.SaveChangesAsync(ct);

            var taken = new SubjectOidcIdentityEntity
            {
                Id = Guid.CreateVersion7(),
                SubjectId = subjectId,
                ProviderId = identity.ProviderId,
                OidcSubjectId = identity.OidcSubjectId,
                Issuer = identity.Issuer,
                Email = email,
                LinkedAt = Now,
            };
            db.SubjectOidcIdentities.Add(taken);
            await db.SaveChangesAsync(ct);

            await tx.CommitAsync(ct);
            return taken.Id;
        });
    }

    /// <summary>
    /// The subjects that may be deleted because nothing names them but their own global roles, the
    /// provider identity <paramref name="identityId"/> and a membership of tenant
    /// <paramref name="ownerOf"/> (each only when given), as far as the tables without a tenant
    /// show, by the rule <see cref="ISubjectRepository.TakeUnclaimedOidcIdentityAsync"/> states.
    /// With <paramref name="ownerOf"/>, only what the setup owner step writes (<see cref="LeftByOwnerStep"/>).
    /// Every table with a foreign key to <c>subjects</c> is named here or in
    /// <see cref="NamedByTenantRowsAsync"/>, and a caller reads both in one transaction, except
    /// <c>tenants.setup_owner_subject_id</c>, which the delete sets to null: a subject that record
    /// names is one the owner step made, and deleting it ends the record.
    /// </summary>
    private static IQueryable<SubjectEntity> Unclaimed(NocturneDbContext db, Guid? identityId, Guid? ownerOf)
    {
        var otherIdentities = identityId is { } kept
            ? db.SubjectOidcIdentities.Where(o => o.Id != kept)
            : db.SubjectOidcIdentities;

        var unclaimed = db.Subjects.Where(s => s.IsActive && !s.IsSystemSubject && !s.IsDemoSubject && !s.IsPlatformAdmin
            && s.LastLoginAt == null
            && !otherIdentities.Any(o => o.SubjectId == s.Id)
            && !db.PasskeyCredentials.Any(c => c.SubjectId == s.Id)
            && !db.TotpCredentials.Any(t => t.SubjectId == s.Id)
            && !db.RecoveryCodes.Any(c => c.SubjectId == s.Id)
            && !db.RefreshTokens.Any(t => t.SubjectId == s.Id)
            && !db.TrustedDevices.Any(d => d.SubjectId == s.Id)
            && !db.TotpStepUpTokens.Any(t => t.SubjectId == s.Id)
            && !db.SubjectAvatars.Any(a => a.SubjectId == s.Id)
            && !db.ChatIdentityDirectory.Any(c => c.NocturneUserId == s.Id)
            && !db.TenantCreationInvites.Any(i => i.CreatedBySubjectId == s.Id)
            && !db.SubjectRoles.Any(r => r.AssignedById == s.Id));

        return ownerOf is { } tenantId
            ? LeftByOwnerStep(db, unclaimed, tenantId)
            : unclaimed.Where(s => !db.TenantMembers.Any(m => m.SubjectId == s.Id));
    }

    /// <summary>
    /// The <paramref name="subjects"/> whose memberships and global roles are what the setup owner
    /// step writes for <paramref name="tenantId"/>: a membership of that tenant holding the owner
    /// role, no membership of any other tenant, and no global role but <see cref="GlobalRoles.Admin"/>.
    /// </summary>
    private static IQueryable<SubjectEntity> LeftByOwnerStep(
        NocturneDbContext db, IQueryable<SubjectEntity> subjects, Guid tenantId) =>
        subjects.Where(s => db.TenantMemberRoles.Any(r => r.TenantMember.SubjectId == s.Id
                && r.TenantMember.TenantId == tenantId && r.TenantRole.Slug == RoleSeeds.Owner)
            && !db.TenantMembers.Any(m => m.SubjectId == s.Id && m.TenantId != tenantId)
            && !db.SubjectRoles.Any(r => r.SubjectId == s.Id && r.Role!.Name != GlobalRoles.Admin));

    /// <summary>
    /// Whether a row of a tenant-scoped table names the subject, in any tenant: of every table with a
    /// foreign key to <c>subjects</c>, and of the subject-id columns without one that a single lookup
    /// checks. Each tenant's reads run with its id set local to the transaction, as
    /// <see cref="ScrubTenantRowsAsync"/> does; the statements after them touch no tenant-scoped table.
    /// </summary>
    private static async Task<bool> NamedByTenantRowsAsync(NocturneDbContext db, Guid subjectId, CancellationToken ct)
    {
        var tenantIds = await db.Tenants.AsNoTracking().Select(t => t.Id).ToListAsync(ct);
        foreach (var tenantId in tenantIds)
        {
            if (db.Database.IsNpgsql())
            {
                await db.Database.ExecuteSqlRawAsync(
                    "SELECT set_config('app.current_tenant_id', {0}, true)", [tenantId.ToString()], ct);
            }

            if (await db.LoginCodes.IgnoreQueryFilters().AnyAsync(c => c.TenantId == tenantId && c.SubjectId == subjectId, ct)
                || await db.MemberInvites.IgnoreQueryFilters().AnyAsync(i => i.TenantId == tenantId && i.CreatedBySubjectId == subjectId, ct)
                || await db.OAuthGrants.IgnoreQueryFilters().AnyAsync(g => g.TenantId == tenantId && (g.SubjectId == subjectId || g.CreatedBySubjectId == subjectId), ct)
                || await db.OAuthAuthorizationCodes.IgnoreQueryFilters().AnyAsync(c => c.TenantId == tenantId && c.SubjectId == subjectId, ct)
                || await db.AlertExcursionMutes.IgnoreQueryFilters().AnyAsync(m => m.TenantId == tenantId && m.SubjectId == subjectId, ct)
                || await db.MembershipRequests.IgnoreQueryFilters().AnyAsync(r => r.TenantId == tenantId && (r.SubjectId == subjectId || r.DecidedBySubjectId == subjectId), ct)
                || await db.ClientDevices.IgnoreQueryFilters().AnyAsync(d => d.TenantId == tenantId && d.SubjectId == subjectId, ct)
                || await db.OAuthDeviceCodes.IgnoreQueryFilters().AnyAsync(c => c.TenantId == tenantId && c.SubjectId == subjectId, ct)
                || await db.AlertInvites.IgnoreQueryFilters().AnyAsync(i => i.TenantId == tenantId && (i.CreatedBy == subjectId || i.UsedBy == subjectId), ct)
                || await db.AlertExcursionReceipts.IgnoreQueryFilters().AnyAsync(r => r.TenantId == tenantId && r.ActorKind == AlertReceiptActorKind.Member && r.ActorId == subjectId, ct))
                return true;
        }

        return false;
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
        return await db.Subjects
            .Where(s => db.WorkingOidcIdentities().Any(i => i.SubjectId == s.Id && i.ProviderId == providerId))
            .WhoCannotSignIn(db, _linkRoute, withoutProvider: providerId)
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
    public virtual async Task<DateTime?> LastModifiedAsync(CancellationToken ct)
    {
        await using var lease = await OpenAsync(ct);
        return await lease.Context.Subjects.AsNoTracking()
            .OrderByDescending(s => s.UpdatedAt)
            .Select(s => (DateTime?)s.UpdatedAt)
            .FirstOrDefaultAsync(ct);
    }

    private static SubjectRecord? ToRecord(SubjectEntity? subject) =>
        subject is null ? null : SubjectMapper.ToRecord(
            subject,
            subject.SubjectRoles.Where(sr => sr.Role is not null).Select(sr => RoleMapper.ToDomainModel(sr.Role!)).ToList());

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

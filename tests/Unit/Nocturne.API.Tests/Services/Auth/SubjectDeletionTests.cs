using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Audit;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Identity;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;
using Nocturne.API.Tests.Services.Identity;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// Covers what <see cref="SubjectDeletion.DeleteAsync"/> leaves of the deleted subject's auth
/// audit trail, and whom it tells of the notices it removed, over SQLite so the scrub's bulk updates
/// and the foreign keys both run for real.
/// </summary>
public class SubjectDeletionTests : IDisposable
{
    private readonly SqliteTestDatabase _db = TestDbContextFactory.CreateSqlite();
    private readonly Guid _deletedId = Guid.CreateVersion7();
    private readonly Guid _adminId = Guid.CreateVersion7();
    private readonly Guid _bystanderId = Guid.CreateVersion7();

    private readonly Guid _selfLoginRow = Guid.CreateVersion7();
    private readonly Guid _actedOnOtherRow = Guid.CreateVersion7();
    private readonly Guid _actedOnByAdminRow = Guid.CreateVersion7();
    private readonly Guid _unrelatedRow = Guid.CreateVersion7();

    public SubjectDeletionTests()
    {
        using var seed = _db.CreateContext();
        seed.Subjects.AddRange(
            new SubjectEntity { Id = _deletedId, Name = "Leaver", IsActive = true },
            new SubjectEntity { Id = _adminId, Name = "Admin", IsActive = true },
            new SubjectEntity { Id = _bystanderId, Name = "Bystander", IsActive = true });
        seed.AuthAuditLog.AddRange(
            Row(_selfLoginRow, AuthAuditEventType.Login, subject: _deletedId, actor: _deletedId, "10.0.0.1"),
            Row(_actedOnOtherRow, AuthAuditEventType.RoleAssigned, subject: _bystanderId, actor: _deletedId, "10.0.0.2"),
            Row(_actedOnByAdminRow, AuthAuditEventType.RoleAssigned, subject: _deletedId, actor: _adminId, "10.0.0.3"),
            Row(_unrelatedRow, AuthAuditEventType.Login, subject: _bystanderId, actor: _bystanderId, "10.0.0.4"));
        seed.SaveChanges();
    }

    [Fact]
    public async Task Delete_KeepsTheSubjectAndActorIdsOnEveryRow()
    {
        (await DeleteAsLeaverAsync()).Should().BeOfType<SubjectChange.Changed>();

        var rows = await ReadRowsAsync();
        rows[_selfLoginRow].TargetSubjectId.Should().Be(_deletedId);
        rows[_selfLoginRow].ActorSubjectId.Should().Be(_deletedId);
        rows[_actedOnOtherRow].ActorSubjectId.Should().Be(_deletedId);
        rows[_actedOnByAdminRow].TargetSubjectId.Should().Be(_deletedId);
    }

    [Fact]
    public async Task Delete_ClearsTheClientOfEveryRowTheSubjectActedOn()
    {
        (await DeleteAsLeaverAsync()).Should().BeOfType<SubjectChange.Changed>();

        var rows = await ReadRowsAsync();
        foreach (var id in new[] { _selfLoginRow, _actedOnOtherRow })
        {
            rows[id].IpAddress.Should().BeNull();
            rows[id].UserAgent.Should().BeNull();
        }
    }

    [Fact]
    public async Task Delete_ClearsTheDetailsOfEveryRowTheSubjectIsPartyTo()
    {
        (await DeleteAsLeaverAsync()).Should().BeOfType<SubjectChange.Changed>();

        var rows = await ReadRowsAsync();
        rows[_selfLoginRow].DetailsJson.Should().BeNull();
        rows[_actedOnOtherRow].DetailsJson.Should().BeNull();
        rows[_actedOnByAdminRow].DetailsJson.Should().BeNull();
    }

    [Fact]
    public async Task Delete_KeepsAnotherActorsClientOnARowAboutTheSubject()
    {
        (await DeleteAsLeaverAsync()).Should().BeOfType<SubjectChange.Changed>();

        var row = (await ReadRowsAsync())[_actedOnByAdminRow];
        row.IpAddress.Should().Be("10.0.0.3");
        row.UserAgent.Should().Be("agent-10.0.0.3");
    }

    [Fact]
    public async Task Delete_LeavesRowsTheSubjectIsNoPartyToUntouched()
    {
        (await DeleteAsLeaverAsync()).Should().BeOfType<SubjectChange.Changed>();

        var row = (await ReadRowsAsync())[_unrelatedRow];
        row.IpAddress.Should().Be("10.0.0.4");
        row.UserAgent.Should().Be("agent-10.0.0.4");
        row.DetailsJson.Should().NotBeNull();
    }

    [Fact]
    public async Task Delete_RecordsTheDeletionAgainstTheDeletedSubjectAndTheCaller()
    {
        (await DeleteAsLeaverAsync()).Should().BeOfType<SubjectChange.Changed>();

        await using var reader = _db.CreateContext();
        var deleted = await reader.AuthAuditLog.SingleAsync(a => a.EventType == AuthAuditEventType.SubjectDeleted);
        deleted.TargetSubjectId.Should().Be(_deletedId);
        deleted.ActorSubjectId.Should().Be(_adminId);
        (await reader.Subjects.AnyAsync(s => s.Id == _deletedId)).Should().BeFalse();
    }

    [Fact]
    public async Task Delete_TellsEachOtherHolderOfARemovedNoticeOnItsOwnSite()
    {
        var (home, away) = await SeedNoticesAsync();
        var told = new List<(Guid Tenant, string UserId, Guid NoticeId)>();

        (await DeleteAsLeaverAsync(Hubs((tenant, userId, notice) => told.Add((tenant, userId, notice.Id)))))
            .Should().BeOfType<SubjectChange.Changed>();

        told.Should().BeEquivalentTo(
            [
                (home.Tenant, _adminId.ToString(), home.Notice),
                (away.Tenant, _bystanderId.ToString(), away.Notice),
            ],
            "each removed notice someone else held is broadcast to them under its site, and none the deleted subject held");
    }

    [Fact]
    public async Task Delete_StillDeletesAndTellsTheOthers_WhenABroadcastFails()
    {
        var (_, away) = await SeedNoticesAsync();
        var told = new List<(Guid Tenant, string UserId, Guid NoticeId)>();

        (await DeleteAsLeaverAsync(Hubs((tenant, userId, notice) =>
        {
            if (userId == _adminId.ToString())
                throw new InvalidOperationException("The hub is gone.");
            told.Add((tenant, userId, notice.Id));
        }))).Should().BeOfType<SubjectChange.Changed>();

        told.Should().Equal((away.Tenant, _bystanderId.ToString(), away.Notice));
        await using var reader = _db.CreateContext();
        (await reader.Subjects.AnyAsync(s => s.Id == _deletedId)).Should().BeFalse();
        (await reader.InAppNotifications.IgnoreQueryFilters().AnyAsync(n => n.SourceId == _deletedId.ToString()))
            .Should().BeFalse();
    }

    /// <summary>
    /// A notice naming the deleted subject on each of two sites, held by the admin on one and the
    /// bystander on the other, beside one the deleted subject held and one that names no one.
    /// </summary>
    private async Task<((Guid Tenant, Guid Notice) Home, (Guid Tenant, Guid Notice) Away)> SeedNoticesAsync()
    {
        var home = (Tenant: Guid.CreateVersion7(), Notice: Guid.CreateVersion7());
        var away = (Tenant: Guid.CreateVersion7(), Notice: Guid.CreateVersion7());
        await using var seed = _db.CreateContext();
        seed.Tenants.AddRange(
            new TenantEntity { Id = home.Tenant, Slug = "home", DisplayName = "Home", IsActive = true },
            new TenantEntity { Id = away.Tenant, Slug = "away", DisplayName = "Away", IsActive = true });
        seed.InAppNotifications.AddRange(
            Notice(home.Notice, home.Tenant, _adminId, NotificationSubjectNaming.AnonymousLoginRequest, _deletedId.ToString()),
            Notice(away.Notice, away.Tenant, _bystanderId, NotificationSubjectNaming.AnonymousLoginRequest, _deletedId.ToString()),
            Notice(Guid.CreateVersion7(), home.Tenant, _deletedId, "system.update_available", "1.2.3"),
            Notice(Guid.CreateVersion7(), away.Tenant, _bystanderId, "sharing.link_rotated", away.Tenant.ToString()));
        await seed.SaveChangesAsync();
        return (home, away);
    }

    private static InAppNotificationEntity Notice(Guid id, Guid tenant, Guid holder, string type, string sourceId) => new()
    {
        Id = id,
        TenantId = tenant,
        UserId = holder.ToString(),
        Type = type,
        Title = type,
        SourceId = sourceId,
    };

    /// <summary>
    /// What a site's scope resolves to broadcast with: a hub that hands <paramref name="told"/> the tenant
    /// the scope was opened for, which is the tenant the real hub groups the broadcast under.
    /// </summary>
    private static ServiceProvider Hubs(Action<Guid, string, InAppNotificationDto> told)
    {
        var services = new ServiceCollection();
        services.AddScoped<ITenantAccessor, HttpContextTenantAccessor>();
        services.AddScoped(sp =>
        {
            var tenant = sp.GetRequiredService<ITenantAccessor>();
            var hub = new Mock<ISignalRBroadcastService>();
            hub.Setup(h => h.BroadcastNotificationArchivedAsync(
                    It.IsAny<string>(), It.IsAny<InAppNotificationDto>(), It.IsAny<NotificationArchiveReason>()))
                .Returns<string, InAppNotificationDto, NotificationArchiveReason>((userId, notice, _) =>
                {
                    told(tenant.TenantId, userId, notice);
                    return Task.CompletedTask;
                });
            return hub.Object;
        });
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task A_refused_deletion_tells_no_one_and_removes_no_notice()
    {
        var (home, _) = await SeedNoticesAsync();
        var members = new TenantMembershipRepository(_db.CreateContext(home.Tenant).AsFactory(), AccountLinkRoutes.Off);
        await members.SeedRolesAsync(home.Tenant, CancellationToken.None);
        var owner = (await members.FindRoleIdBySlugAsync(home.Tenant, RoleSeeds.Owner, CancellationToken.None))!.Value;
        (await members.AddMemberAsync(
                new NewTenantMember(home.Tenant, _deletedId, [owner]) { How = MembershipJoinHow.Direct, By = null },
                CancellationToken.None))
            .Should().BeTrue();
        await using (var seed = _db.CreateContext())
        {
            seed.PasskeyCredentials.Add(new PasskeyCredentialEntity
            {
                Id = Guid.CreateVersion7(), SubjectId = _deletedId, CredentialId = _deletedId.ToByteArray(), PublicKey = [1],
            });
            await seed.SaveChangesAsync();
        }

        var told = new List<Guid>();
        (await DeleteAsLeaverAsync(Hubs((_, _, notice) => told.Add(notice.Id)))).Should().BeOfType<SubjectChange.LastOwnerOf>();

        told.Should().BeEmpty("a refused deletion removed nothing");
        await using var reader = _db.CreateContext();
        (await reader.InAppNotifications.IgnoreQueryFilters().AnyAsync(n => n.Id == home.Notice)).Should().BeTrue();
    }

    private async Task<SubjectChange> DeleteAsLeaverAsync(IServiceProvider? hubs = null)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["AuthContext"] = new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.SessionCookie,
            SubjectId = _adminId,
        };

        await using var dbContext = _db.CreateContext();
        var audit = new AuthAuditService(
            dbContext.AsFactory(),
            new HttpContextAccessor { HttpContext = httpContext },
            new AuditContext(),
            NullLogger<AuthAuditService>.Instance);
        var deletion = new SubjectDeletion(
            dbContext.AsFactory(),
            new SubjectRepository(dbContext.AsFactory(), AccountLinkRoutes.Off),
            new TenantMembershipRepository(dbContext.AsFactory(), AccountLinkRoutes.Off),
            new GrantRevocationService(new GuestSessionCacheService(new MemoryCache(new MemoryCacheOptions()))),
            audit,
            TimeProvider.System,
            InertTeamStaff.Announcer(),
            hubs is null ? InertTeamStaff.Sites() : new SiteNotices(new TenantRepository(dbContext.AsFactory()), hubs),
            NullLogger<SubjectDeletion>.Instance);

        return (await deletion.DeleteAsync(_deletedId, evenLastOwner: false, CancellationToken.None)).Change;
    }

    private async Task<Dictionary<Guid, AuthAuditLogEntity>> ReadRowsAsync()
    {
        await using var reader = _db.CreateContext();
        return await reader.AuthAuditLog.AsNoTracking().ToDictionaryAsync(a => a.Id);
    }

    private static AuthAuditLogEntity Row(Guid id, string eventType, Guid subject, Guid actor, string ip) => new()
    {
        Id = id,
        EventType = eventType,
        TargetSubjectId = subject,
        ActorSubjectId = actor,
        IpAddress = ip,
        UserAgent = $"agent-{ip}",
        DetailsJson = """{"email":"someone@example.invalid"}""",
    };

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }
}

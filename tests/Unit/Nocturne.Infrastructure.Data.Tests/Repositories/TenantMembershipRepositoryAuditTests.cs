using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// The audit rows <see cref="TenantMembershipRepository"/> writes with each change to a member's
/// roles, direct permissions and 24-hour limit: who they name, who they blame, and that they commit
/// and roll back with the change.
/// </summary>
public class TenantMembershipRepositoryAuditTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 31, 9, 0, 0, DateTimeKind.Utc);

    private readonly Guid _tenant = Guid.CreateVersion7();
    private readonly Guid _subject = Guid.CreateVersion7();
    private readonly Guid _actor = Guid.CreateVersion7();
    private readonly Guid _viewer = Guid.CreateVersion7();
    private readonly Guid _caretaker = Guid.CreateVersion7();
    private readonly SqliteTestDatabase _db;
    private Guid _member;

    public TenantMembershipRepositoryAuditTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(_tenant);
        using var db = _db.CreateContext(_tenant);
        db.Subjects.AddRange(
            new SubjectEntity { Id = _subject, Name = "Member" },
            new SubjectEntity { Id = _actor, Name = "Actor" });
        db.TenantRoles.AddRange(Role(_viewer, RoleSeeds.Viewer), Role(_caretaker, RoleSeeds.Caretaker));
        db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private TenantRoleEntity Role(Guid id, string slug) => new()
    {
        Id = id, TenantId = _tenant, Name = slug, Slug = slug, Permissions = [Scope.GlucoseRead], IsSystem = true,
    };

    private NocturneDbContext Context(IAuditContext? actor)
    {
        var context = _db.CreateContext(_tenant);
        context.ActingAs(actor);
        return context;
    }

    private TenantMembershipRepository Repository(IAuditContext? actor) =>
        new(new TestNocturneDbContextFactory(Context(actor)), AccountLinkRoutes.Off);

    private Task<TenantRoleUpdate> EditViewerAsync(IAuditContext actor, TenantRoleEdit edit) =>
        TeamWrites.EditRoleAsync(new TestNocturneDbContextFactory(Context(actor)), _tenant, _viewer, edit, editorSubjectId: null, Now);

    private async Task SeedMemberAsync()
    {
        await using var db = _db.CreateContext(_tenant);
        _member = Guid.CreateVersion7();
        db.TenantMembers.Add(new TenantMemberEntity
        {
            Id = _member, TenantId = _tenant, SubjectId = _subject, DirectPermissions = [Scope.GlucoseRead],
        });
        db.TenantMemberRoles.Add(new TenantMemberRoleEntity
        {
            Id = Guid.CreateVersion7(), TenantMemberId = _member, TenantRoleId = _viewer,
        });
        await db.SaveChangesAsync();
    }

    private async Task<List<AuthAuditLogEntity>> RowsAsync()
    {
        await using var db = _db.CreateContext(_tenant);
        return await db.AuthAuditLog.AsNoTracking().ToListAsync();
    }

    [Fact]
    public async Task ASetOfRolesThatEndsWhereItStartedAuditsNothing()
    {
        await SeedMemberAsync();

        await Repository(new TestAuditActor { SubjectId = _actor })
            .ReplaceRolesAsync(_tenant, _member, [_viewer], [_viewer], Now, CancellationToken.None);

        (await RowsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ARoleSwapAuditsTheRoleRemovedAndTheRoleAdded()
    {
        await SeedMemberAsync();

        await Repository(new TestAuditActor { SubjectId = _actor })
            .ReplaceRolesAsync(_tenant, _member, [_viewer], [_caretaker], Now, CancellationToken.None);

        var rows = await RowsAsync();
        rows.Should().HaveCount(2);
        rows.Select(a => a.DetailsJson).Should().BeEquivalentTo(
            """{"role":"viewer","kind":"tenant"}""",
            """{"role":"caretaker","kind":"tenant"}""");
        rows.Single(a => a.EventType == AuthAuditEventType.RoleRemoved).DetailsJson.Should().Contain("viewer");
        rows.Single(a => a.EventType == AuthAuditEventType.RoleAssigned).DetailsJson.Should().Contain("caretaker");
    }

    [Fact]
    public async Task AWriteWithNoScopeActorIsAttributedToTheSystem()
    {
        await SeedMemberAsync();

        await Repository(actor: null)
            .SetLimitTo24HoursAsync(_tenant, _member, true, Now, CancellationToken.None);

        var row = (await RowsAsync()).Should().ContainSingle().Subject;
        row.ActorSubjectId.Should().BeNull();
        row.ActorCredential.Should().Be("system");
        row.TargetSubjectId.Should().Be(_subject);
    }

    [Fact]
    public async Task ASystemAttributedScopeIsNamedByItsAuthType()
    {
        await SeedMemberAsync();

        await Repository(new TestAuditActor { SubjectId = _actor, AuthType = "system", IsSystem = true })
            .SetLimitTo24HoursAsync(_tenant, _member, true, Now, CancellationToken.None);

        var row = (await RowsAsync()).Should().ContainSingle().Subject;
        row.ActorSubjectId.Should().BeNull("a system-attributed stretch has no human actor");
        row.ActorCredential.Should().Be("system");
    }

    [Fact]
    public async Task ACredentialWithNoSubjectIsNamedByItsTypeAndToken()
    {
        await SeedMemberAsync();
        var token = Guid.CreateVersion7();

        await Repository(new TestAuditActor { AuthType = "ApiKey", TokenId = token })
            .SetDirectPermissionsAsync(_tenant, _member, [Scope.DevicesRead], Now, CancellationToken.None);

        (await RowsAsync()).Should().OnlyContain(a => a.ActorSubjectId == null && a.ActorCredential == $"ApiKey:{token}");
    }

    [Fact]
    public async Task ACredentialWithNoSubjectOrTokenIsNamedByItsType()
    {
        await SeedMemberAsync();

        await Repository(new TestAuditActor { AuthType = "ApiKey" })
            .SetLimitTo24HoursAsync(_tenant, _member, true, Now, CancellationToken.None);

        (await RowsAsync()).Should().ContainSingle().Which.ActorCredential.Should().Be("ApiKey");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("None")]
    public async Task AScopeWithNoCallerIsRecordedAsAnonymousNotAsACredential(string? authType)
    {
        await SeedMemberAsync();

        await Repository(new TestAuditActor { AuthType = authType })
            .SetLimitTo24HoursAsync(_tenant, _member, true, Now, CancellationToken.None);

        var row = (await RowsAsync()).Should().ContainSingle().Subject;
        row.ActorSubjectId.Should().BeNull();
        row.ActorCredential.Should().Be("anonymous");
    }

    [Fact]
    public async Task ACredentialWithAFingerprintIsNamedByItBeforeItsToken()
    {
        await SeedMemberAsync();

        await Repository(new TestAuditActor { AuthType = "InstanceKey", CredentialFingerprint = "a1b2c3", TokenId = Guid.CreateVersion7() })
            .SetLimitTo24HoursAsync(_tenant, _member, true, Now, CancellationToken.None);

        (await RowsAsync()).Should().ContainSingle().Which.ActorCredential.Should().Be("InstanceKey:a1b2c3");
    }

    [Fact]
    public async Task TheRowKeepsTheRequestsAddressUserAgentAndTrace()
    {
        await SeedMemberAsync();

        await Repository(new TestAuditActor
            {
                SubjectId = _actor, IpAddress = "10.0.0.7", UserAgent = "Browser/1.0", TraceId = "trace-1",
            })
            .SetLimitTo24HoursAsync(_tenant, _member, true, Now, CancellationToken.None);

        (await RowsAsync()).Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            ActorSubjectId = (Guid?)_actor, IpAddress = "10.0.0.7", UserAgent = "Browser/1.0", TraceId = "trace-1",
        });
    }

    [Fact]
    public async Task TheHistoryLimitIsItsOwnEventNamingTheLimitAfterTheChange()
    {
        await SeedMemberAsync();
        var repository = Repository(new TestAuditActor { SubjectId = _actor });

        await repository.SetLimitTo24HoursAsync(_tenant, _member, true, Now, CancellationToken.None);
        await repository.SetLimitTo24HoursAsync(_tenant, _member, true, Now, CancellationToken.None);
        await Repository(new TestAuditActor { SubjectId = _actor })
            .SetLimitTo24HoursAsync(_tenant, _member, false, Now, CancellationToken.None);

        (await RowsAsync()).OrderBy(a => a.Id).Select(a => (a.EventType, a.DetailsJson)).Should().Equal(
            (AuthAuditEventType.HistoryLimitChanged, """{"history_limit":"24_hours","kind":"tenant"}"""),
            (AuthAuditEventType.HistoryLimitChanged, """{"history_limit":"full","kind":"tenant"}"""));
    }

    [Fact]
    public async Task AMembershipMadeByTheMemberNamesThemAsTheActorWithTheInviteAsTheCause()
    {
        var invite = Guid.CreateVersion7();
        await using (var db = _db.CreateContext(_tenant))
        {
            db.MemberInvites.Add(new MemberInviteEntity
            {
                Id = invite, TenantId = _tenant, CreatedBySubjectId = _actor, TokenHash = "hash", RoleIds = [_viewer],
                ExpiresAt = Now.AddDays(1),
            });
            await db.SaveChangesAsync();
        }

        await Repository(new TestAuditActor { AuthType = "None" }).AddMemberAsync(
            new NewTenantMember(_tenant, _subject, [_viewer], [Scope.DevicesRead],
                CreatedFromInviteId: invite, MadeBy: MembershipMadeBy.Member) { How = MembershipJoinHow.Direct, By = null },
            CancellationToken.None);

        var rows = await RowsAsync();
        rows.Should().HaveCount(3).And.OnlyContain(a =>
            a.ActorSubjectId == _subject && a.ActorCredential == null && a.TargetSubjectId == _subject);
        rows.Select(a => a.DetailsJson).Should().BeEquivalentTo(
            $$"""{"kind":"tenant","how":"direct","invite":"{{invite}}"}""",
            $$"""{"role":"viewer","kind":"tenant","invite":"{{invite}}"}""",
            $$"""{"permission":"devices.read","kind":"tenant","invite":"{{invite}}"}""");
    }

    [Fact]
    public async Task EditingARolesPermissionsAuditsTheGainedAndLostOnTheRoleWithTheActor()
    {
        var changed = new TenantRoleEdit("viewer", RoleSeeds.Viewer, null, [Scope.TreatmentsRead, Scope.DevicesRead]);

        await EditViewerAsync(new TestAuditActor { SubjectId = _actor }, changed);

        var row = (await RowsAsync()).Should().ContainSingle().Subject;
        row.EventType.Should().Be(AuthAuditEventType.RolePermissionsChanged);
        row.ActorSubjectId.Should().Be(_actor);
        row.TargetSubjectId.Should().BeNull();
        row.TenantId.Should().Be(_tenant);
        row.DetailsJson.Should().Be(
            $$"""{"role_id":"{{_viewer}}","role":"viewer","gained":["devices.read","treatments.read"],"lost":["glucose.read"],"kind":"tenant"}""");
    }

    [Fact]
    public async Task EditingARoleWithoutChangingItsPermissionsAuditsNothing()
    {
        await EditViewerAsync(
            new TestAuditActor { SubjectId = _actor }, new TenantRoleEdit("Renamed", "renamed", "described", [Scope.GlucoseRead]));

        (await RowsAsync()).Should().BeEmpty();
        await using var db = _db.CreateContext(_tenant);
        (await db.TenantRoles.AsNoTracking().SingleAsync(r => r.Id == _viewer)).Name.Should().Be("Renamed");
    }

    [Fact]
    public async Task ReorderingARolesPermissionsAuditsNothing()
    {
        await using (var db = _db.CreateContext(_tenant))
        {
            (await db.TenantRoles.SingleAsync(r => r.Id == _viewer)).Permissions = [Scope.GlucoseRead, Scope.TreatmentsRead];
            await db.SaveChangesAsync();
        }

        await EditViewerAsync(
            new TestAuditActor { SubjectId = _actor }, new TenantRoleEdit("viewer", RoleSeeds.Viewer, null, [Scope.TreatmentsRead, Scope.GlucoseRead]));

        (await RowsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ARoleEditThatIsRolledBackLeavesNoAuditRow()
    {
        await using var unit = await DbUnitOfWork.OpenForTenantAsync(
            new TestNocturneDbContextFactory(Context(new TestAuditActor { SubjectId = _actor })), _tenant, CancellationToken.None);

        var act = () => unit.CommitAsync(async token =>
        {
            await TenantMembershipRepository.LockRoleEditAsync(
                unit, _tenant, _viewer, new TenantRoleEdit("viewer", RoleSeeds.Viewer, null, [Scope.DevicesRead]), editorSubjectId: null, Now, token);
            throw new InvalidOperationException("the rest of the unit failed");
        });

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await RowsAsync()).Should().BeEmpty();
        await using var db = _db.CreateContext(_tenant);
        (await db.TenantRoles.AsNoTracking().SingleAsync(r => r.Id == _viewer)).Permissions.Should().BeEquivalentTo([Scope.GlucoseRead]);
    }

    [Fact]
    public async Task DeletingARoleAuditsItsRemovalFromEveryHolder()
    {
        await SeedMemberAsync();
        var other = Guid.CreateVersion7();
        await using (var db = _db.CreateContext(_tenant))
        {
            db.Subjects.Add(new SubjectEntity { Id = other, Name = "Other" });
            var otherMember = Guid.CreateVersion7();
            db.TenantMembers.Add(new TenantMemberEntity { Id = otherMember, TenantId = _tenant, SubjectId = other });
            db.TenantMemberRoles.Add(new TenantMemberRoleEntity
            {
                Id = Guid.CreateVersion7(), TenantMemberId = otherMember, TenantRoleId = _viewer,
            });
            await db.SaveChangesAsync();
        }

        await Repository(new TestAuditActor { SubjectId = _actor }).DeleteRoleAsync(_tenant, _viewer, Now, CancellationToken.None);

        var rows = await RowsAsync();
        rows.Select(a => a.TargetSubjectId).Should().BeEquivalentTo([(Guid?)_subject, other]);
        rows.Should().OnlyContain(a => a.EventType == AuthAuditEventType.RoleRemoved
            && a.DetailsJson == """{"role":"viewer","kind":"tenant"}""" && a.ActorSubjectId == _actor);
    }

    [Fact]
    public async Task RemovingAMemberAuditsEveryRoleAndPermissionItHeld()
    {
        await SeedMemberAsync();

        await OwnerGuardedWrites.RemoveMemberUnlessLastOwnerAsync(
            new TestNocturneDbContextFactory(Context(new TestAuditActor { SubjectId = _actor })), AccountLinkRoutes.Off,
            _tenant, _member, MembershipEndHow.Removed, Now, CancellationToken.None);

        var rows = await RowsAsync();
        rows.Should().OnlyContain(a => (a.EventType == AuthAuditEventType.RoleRemoved || a.EventType == AuthAuditEventType.MemberRemoved)
            && a.TargetSubjectId == _subject);
        rows.Select(a => a.DetailsJson).Should().BeEquivalentTo(
            """{"role":"viewer","kind":"tenant"}""",
            """{"permission":"glucose.read","kind":"tenant"}""",
            """{"kind":"tenant","how":"removed"}""");
    }

    [Fact]
    public async Task RemovingASubjectsMembershipsAuditsEachOne()
    {
        await SeedMemberAsync();

        await Repository(new TestAuditActor { SubjectId = _actor })
            .RemoveMembershipsOfSubjectAsync(_subject, CancellationToken.None);

        var rows = await RowsAsync();
        rows.Should().OnlyContain(a => (a.EventType == AuthAuditEventType.RoleRemoved || a.EventType == AuthAuditEventType.MemberRemoved)
            && a.TargetSubjectId == _subject && a.TenantId == _tenant);
        rows.Select(a => a.DetailsJson).Should().BeEquivalentTo(
            """{"role":"viewer","kind":"tenant"}""",
            """{"permission":"glucose.read","kind":"tenant"}""",
            """{"kind":"tenant","how":"system"}""");
    }

    [Fact]
    public async Task AChangeCommittedOnAUnitIsAuditedInTheSameCommit()
    {
        await SeedMemberAsync();
        await using var unit = await DbUnitOfWork.OpenForTenantAsync(
            new TestNocturneDbContextFactory(Context(new TestAuditActor { SubjectId = _actor })), _tenant, CancellationToken.None);
        var repository = new TenantMembershipRepository(unit, AccountLinkRoutes.Off);

        await unit.CommitAsync(async token =>
        {
            await repository.SetDirectPermissionsAsync(_tenant, _member, [Scope.DevicesRead], Now, token);
            (await RowsAsync()).Should().BeEmpty("the unit has not saved yet");
            await unit.Context.SaveChangesAsync(token);
        });

        (await RowsAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task AChangeThatIsRolledBackLeavesNoAuditRow()
    {
        await SeedMemberAsync();
        await using var unit = await DbUnitOfWork.OpenForTenantAsync(
            new TestNocturneDbContextFactory(Context(new TestAuditActor { SubjectId = _actor })), _tenant, CancellationToken.None);
        var repository = new TenantMembershipRepository(unit, AccountLinkRoutes.Off);

        var act = () => unit.CommitAsync(async token =>
        {
            await repository.SetLimitTo24HoursAsync(_tenant, _member, true, Now, token);
            await unit.Context.SaveChangesAsync(token);
            throw new InvalidOperationException("the rest of the unit failed");
        });

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await RowsAsync()).Should().BeEmpty();
        await using var db = _db.CreateContext(_tenant);
        (await db.TenantMembers.AsNoTracking().SingleAsync(m => m.Id == _member)).LimitTo24Hours.Should().BeFalse();
    }
}

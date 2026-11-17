using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// <see cref="TenantMembershipRepository"/> over SQLite: memberships with their roles, the tenant's
/// roles and requests, the one-statement reads the request pipeline makes, and the writes staged on
/// a unit of work until its owner commits. The pins are the Postgres tests' concern.
/// </summary>
public class TenantMembershipRepositoryTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 31, 9, 0, 0, DateTimeKind.Utc);

    private readonly Guid _tenant = Guid.CreateVersion7();
    private readonly Guid _otherTenant = Guid.CreateVersion7();
    private readonly SqliteTestDatabase _db;
    private readonly Guid _ownerRole;
    private readonly Guid _caretakerRole;

    public TenantMembershipRepositoryTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(_tenant, "home").SeedTenant(_otherTenant, "other");
        Members().SeedRolesAsync(_tenant, CancellationToken.None).GetAwaiter().GetResult();
        _ownerRole = Members().FindRoleIdBySlugAsync(_tenant, RoleSeeds.Owner, CancellationToken.None).GetAwaiter().GetResult()!.Value;
        _caretakerRole = Members().FindRoleIdBySlugAsync(_tenant, RoleSeeds.Caretaker, CancellationToken.None).GetAwaiter().GetResult()!.Value;
    }

    public void Dispose() => _db.Dispose();

    private TenantMembershipRepository Members(Guid? tenant = null) =>
        new(new TestNocturneDbContextFactory(_db.CreateContext(tenant ?? _tenant)));

    private SubjectRepository Subjects() => new(new TestNocturneDbContextFactory(_db.CreateContext(Guid.Empty)));

    private Task<Guid> SubjectAsync(string name, bool system = false, bool active = true, bool demo = false) =>
        Subjects().CreateAsync(new NewSubject(name, IsSystemSubject: system, IsActive: active, IsDemoSubject: demo), CancellationToken.None);

    private async Task<TenantMemberRecord> JoinAsync(Guid subject, params Guid[] roles)
    {
        (await Members().AddMemberAsync(new NewTenantMember(_tenant, subject, roles), CancellationToken.None)).Should().BeTrue();
        return (await Members().FindMemberAsync(_tenant, subject, CancellationToken.None))!;
    }

    private void AddPasskey(Guid subject)
    {
        using var db = _db.CreateContext();
        db.PasskeyCredentials.Add(new PasskeyCredentialEntity
        {
            Id = Guid.CreateVersion7(), SubjectId = subject, CredentialId = Guid.NewGuid().ToByteArray(), PublicKey = [1],
        });
        db.SaveChanges();
    }

    private void SetJoinedAt(Guid memberId, DateTime at)
    {
        using var db = _db.CreateContext();
        db.TenantMembers.Single(m => m.Id == memberId).SysCreatedAt = at;
        db.SaveChanges();
    }

    [Fact]
    public async Task TheSeedRolesAreStoredOnceAndReadBackWithTheirMemberCounts()
    {
        await Members().SeedRolesAsync(_tenant, CancellationToken.None);

        var roles = await Members().ListRolesAsync(_tenant, CancellationToken.None);
        roles.Select(r => r.Slug).Should().BeEquivalentTo(RoleSeeds.Permissions.Keys);
        roles.Should().OnlyContain(r => r.IsSystem && r.MemberCount == 0 && r.TenantId == _tenant);
        (await Members().ListRolesAsync(_otherTenant, CancellationToken.None)).Should().BeEmpty();
        (await Members().GetRoleAsync(_otherTenant, _ownerRole, CancellationToken.None)).Should().BeNull("a role is keyed by its tenant too");
        (await Members().ListRolePermissionsAsync(_tenant, [_ownerRole, _ownerRole, Guid.CreateVersion7()], CancellationToken.None))
            .Should().ContainSingle().Which.Should().Equal(Scope.FullAccess);
        (await Members().ListRolePermissionsAsync(_tenant, [], CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task AMembershipReadsBackWithItsSubjectAndRolesAndIsAddedOnce()
    {
        var subject = await SubjectAsync("Rhys");
        (await Members().AddMemberAsync(
            new NewTenantMember(_tenant, subject, [_ownerRole, _caretakerRole], ["extra.read"], "Label", "rhys", LimitTo24Hours: true), CancellationToken.None))
            .Should().BeTrue();

        var member = await Members().FindMemberAsync(_tenant, subject, CancellationToken.None);
        member.Should().BeEquivalentTo(new
        {
            TenantId = _tenant, SubjectId = subject, SubjectName = "Rhys", SubjectIsActive = true, IsSystemSubject = false,
            IsPlatformAdmin = false, Label = "Label", Username = "rhys", LimitTo24Hours = true, IsOwner = true,
        });
        member!.Roles.Select(r => r.Slug).Should().BeEquivalentTo(RoleSeeds.Owner, RoleSeeds.Caretaker);
        member.EffectivePermissions().Should().Contain(Scope.FullAccess).And.Contain("extra.read");
        member.EffectivePermissions(excludingRoleId: _ownerRole).Should().NotContain(Scope.FullAccess);
        member.Holds(Scope.MembersManage).Should().BeTrue();

        (await Members().AddMemberAsync(new NewTenantMember(_tenant, subject, []), CancellationToken.None)).Should().BeFalse("already a member");
        (await Members().GetMemberAsync(_tenant, member.Id, CancellationToken.None))!.Id.Should().Be(member.Id);
        (await Members().GetMemberAsync(_otherTenant, member.Id, CancellationToken.None)).Should().BeNull();
        (await Members().FindMemberByUsernameAsync(_tenant, "rhys", CancellationToken.None))!.Id.Should().Be(member.Id);
        (await Members().UsernameExistsAsync(_tenant, "rhys", CancellationToken.None)).Should().BeTrue();
        (await Members().UsernameExistsAsync(_tenant, "nobody", CancellationToken.None)).Should().BeFalse();
        (await Members().UsernameExistsAsync(_otherTenant, "rhys", CancellationToken.None)).Should().BeFalse();
        (await Members().IsMemberAsync(_tenant, subject, CancellationToken.None)).Should().BeTrue();
        (await Members().IsMemberAsync(_otherTenant, subject, CancellationToken.None)).Should().BeFalse();
        (await Members().GetMemberRoleNamesAsync(_tenant, subject, CancellationToken.None)).Should().BeEquivalentTo("Owner", "Caretaker");
        (await Members().GetMemberAccessAsync(member.Id, CancellationToken.None)).Should().BeEquivalentTo(new { LimitTo24Hours = true });
        (await Members().GetMemberAccessAsync(_tenant, subject, CancellationToken.None))!.EffectivePermissions.Should().Contain("extra.read");
        (await Members().ListRolesAsync(_tenant, CancellationToken.None)).Single(r => r.Id == _ownerRole).MemberCount.Should().Be(1);
    }

    [Fact]
    public async Task AMembershipRecordsTheInviteItWasAcceptedThrough()
    {
        var inviter = await SubjectAsync("Inviter");
        var invite = Guid.CreateVersion7();
        await using (var seed = _db.CreateContext())
        {
            seed.MemberInvites.Add(new MemberInviteEntity
            {
                Id = invite, TenantId = _tenant, CreatedBySubjectId = inviter, TokenHash = "hash", RoleIds = [_caretakerRole], ExpiresAt = Now.AddDays(1),
            });
            await seed.SaveChangesAsync();
        }

        var subject = await SubjectAsync("Invited");
        (await Members().AddMemberAsync(
            new NewTenantMember(_tenant, subject, [_caretakerRole], CreatedFromInviteId: invite), CancellationToken.None)).Should().BeTrue();

        (await Members().FindMemberAsync(_tenant, subject, CancellationToken.None))!.CreatedFromInviteId.Should().Be(invite);
    }

    [Fact]
    public async Task AMembershipThatBreaksAForeignKeyThrowsRatherThanReportingAnExistingMember()
    {
        var subject = await SubjectAsync("Rejected");

        var act = () => Members().AddMemberAsync(
            new NewTenantMember(_tenant, subject, [Guid.CreateVersion7()]), CancellationToken.None);

        await act.Should().ThrowAsync<DbUpdateException>();
        (await Members().IsMemberAsync(_tenant, subject, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task ARoleNamedTwiceIsGrantedOnceAndTheSubjectIsNotReportedAsAMember()
    {
        var subject = await SubjectAsync("Doubled");

        (await Members().AddMemberAsync(
            new NewTenantMember(_tenant, subject, [_caretakerRole, _caretakerRole]), CancellationToken.None)).Should().BeTrue();

        (await Members().FindMemberAsync(_tenant, subject, CancellationToken.None))!
            .Roles.Select(r => r.RoleId).Should().Equal(_caretakerRole);
    }

    [Fact]
    public async Task PeopleAndOwnersLeaveOutSystemAndDeactivatedSubjectsInJoinOrder()
    {
        var first = await JoinAsync(await SubjectAsync("First"), _ownerRole);
        var second = await JoinAsync(await SubjectAsync("Second"), _ownerRole);
        var caretaker = await JoinAsync(await SubjectAsync("Care"), _caretakerRole);
        var publicMember = await JoinAsync(await SubjectAsync("Public", system: true));
        await JoinAsync(await SubjectAsync("Gone", active: false), _ownerRole);
        SetJoinedAt(first.Id, Now);
        SetJoinedAt(second.Id, Now.AddMinutes(1));
        SetJoinedAt(caretaker.Id, Now.AddMinutes(2));

        (await Members().ListPeopleAsync(_tenant, CancellationToken.None)).Select(m => m.SubjectName).Should().Equal("First", "Second", "Care");
        (await Members().ListOwnersAsync(_tenant, CancellationToken.None)).Select(m => m.SubjectName).Should().Equal("First", "Second");
        (await Members().GetOwnerSubjectIdsAsync(_tenant, CancellationToken.None)).Should().Equal(first.SubjectId, second.SubjectId);
        (await Members().FindPublicMemberAsync(_tenant, CancellationToken.None))!.Id.Should().Be(publicMember.Id);
        (await Members().ListMembersAsync(_tenant, CancellationToken.None)).Should().HaveCount(5);

        (await Members().AnyOtherOwnerAsync(_tenant, first.Id, CancellationToken.None)).Should().BeTrue();
        (await Members().RemoveMemberUnlessLastOwnerAsync(_tenant, second.Id, CancellationToken.None)).Should().Be(OwnerGuardedChange.Changed);
        (await Members().RemoveMemberUnlessLastOwnerAsync(_tenant, first.Id, CancellationToken.None))
            .Should().Be(OwnerGuardedChange.LastOwner, "the deactivated owner does not count");
        (await Members().ReplaceRolesUnlessLastOwnerAsync(_tenant, first.Id, [_ownerRole], [_caretakerRole], Now, CancellationToken.None))
            .Should().Be(OwnerGuardedChange.LastOwner);
        (await Members().AnyOtherOwnerAsync(_tenant, first.Id, CancellationToken.None)).Should().BeFalse();
        (await Members().ReplaceRolesUnlessLastOwnerAsync(_tenant, caretaker.Id, [_caretakerRole], [], Now, CancellationToken.None))
            .Should().Be(OwnerGuardedChange.Changed, "not an owner");

        await Subjects().UpdateAsync(first.SubjectId, new SubjectEdit(Preferences: "{\"units\":\"mmol\"}"), CancellationToken.None);
        (await Members().GetEarliestOwnerPreferencesAsync(_tenant, CancellationToken.None)).Should().Be("{\"units\":\"mmol\"}");
    }

    [Fact]
    public async Task FindPublicMemberIgnoresAPersonNamedPublic()
    {
        var person = await JoinAsync(await SubjectAsync(PublicSubjectFilter.PublicSubjectName), _ownerRole);
        var publicMember = await JoinAsync(await SubjectAsync(PublicSubjectFilter.PublicSubjectName, system: true));

        (await Members().FindPublicMemberAsync(_tenant, CancellationToken.None))!.Id.Should().Be(publicMember.Id)
            .And.NotBe(person.Id);
    }

    [Fact]
    public async Task TheEditsWriteOneColumnEachAndStampTheMembership()
    {
        var member = await JoinAsync(await SubjectAsync("Edited"), _caretakerRole);

        (await Members().SetDirectPermissionsAsync(_tenant, member.Id, ["glucose.read"], Now, CancellationToken.None)).Should().BeTrue();
        (await Members().SetLimitTo24HoursAsync(_tenant, member.Id, true, Now, CancellationToken.None)).Should().BeTrue();
        (await Members().SetUsernameAsync(_tenant, member.Id, "edited", CancellationToken.None)).Should().BeTrue();
        (await Members().ReplaceRolesAsync(_tenant, member.Id, [_caretakerRole], [_ownerRole], Now, CancellationToken.None)).Should().BeTrue();

        var edited = await Members().GetMemberAsync(_tenant, member.Id, CancellationToken.None);
        edited.Should().BeEquivalentTo(new
        {
            DirectPermissions = new List<string> { "glucose.read" }, LimitTo24Hours = true, Username = "edited",
        });
        edited!.Roles.Select(r => r.RoleId).Should().Equal(_ownerRole);
        await using (var check = _db.CreateContext())
            (await check.TenantMembers.SingleAsync(m => m.Id == member.Id)).SysUpdatedAt.Should().Be(Now);

        (await Members().SetDirectPermissionsAsync(_tenant, member.Id, null, Now, CancellationToken.None)).Should().BeTrue();
        (await Members().GetMemberAsync(_tenant, member.Id, CancellationToken.None))!.DirectPermissions.Should().BeNull();

        var missing = Guid.CreateVersion7();
        (await Members().SetDirectPermissionsAsync(_tenant, missing, [], Now, CancellationToken.None)).Should().BeFalse();
        (await Members().SetLimitTo24HoursAsync(_tenant, missing, true, Now, CancellationToken.None)).Should().BeFalse();
        (await Members().SetUsernameAsync(_tenant, missing, "x", CancellationToken.None)).Should().BeFalse();
        (await Members().ReplaceRolesAsync(_tenant, missing, [], [], Now, CancellationToken.None)).Should().BeFalse();
        (await Members().ReplaceRolesUnlessLastOwnerAsync(_tenant, missing, [_ownerRole], [], Now, CancellationToken.None))
            .Should().Be(OwnerGuardedChange.NotFound);
        (await Members().RemoveMemberUnlessLastOwnerAsync(_tenant, missing, CancellationToken.None)).Should().Be(OwnerGuardedChange.NotFound);
    }

    [Fact]
    public async Task TheUseStampIsOneStatementOnTheRow()
    {
        var member = await JoinAsync(await SubjectAsync("Used"), _caretakerRole);

        await Members().RecordUseAsync(_tenant, member.Id, Now, "10.0.0.1", "agent", CancellationToken.None);

        await using var check = _db.CreateContext();
        (await check.TenantMembers.SingleAsync(m => m.Id == member.Id)).Should().BeEquivalentTo(new
        {
            LastUsedAt = (DateTime?)Now, LastUsedIp = "10.0.0.1", LastUsedUserAgent = "agent",
        });
    }

    [Fact]
    public async Task AUseStampWithinTheIntervalOfTheStoredOneChangesNothing()
    {
        var member = await JoinAsync(await SubjectAsync("Used"), _caretakerRole);
        await Members().RecordUseAsync(_tenant, member.Id, Now, "10.0.0.1", "agent", CancellationToken.None);

        await Members().RecordUseAsync(
            _tenant, member.Id, Now + LastUsedStamp.Interval, "10.0.0.2", "other", CancellationToken.None);

        await using (var held = _db.CreateContext())
        {
            (await held.TenantMembers.SingleAsync(m => m.Id == member.Id)).Should().BeEquivalentTo(new
            {
                LastUsedAt = (DateTime?)Now, LastUsedIp = "10.0.0.1", LastUsedUserAgent = "agent",
            });
        }

        var due = Now + LastUsedStamp.Interval + TimeSpan.FromSeconds(1);
        await Members().RecordUseAsync(_tenant, member.Id, due, "10.0.0.2", "other", CancellationToken.None);

        await using var stamped = _db.CreateContext();
        (await stamped.TenantMembers.SingleAsync(m => m.Id == member.Id)).Should().BeEquivalentTo(new
        {
            LastUsedAt = (DateTime?)due, LastUsedIp = "10.0.0.2", LastUsedUserAgent = "other",
        });
    }

    [Fact]
    public async Task TheRequestReadJoinsTheSubjectFlagToTheMembership()
    {
        var subject = await SubjectAsync("Member");
        var member = await JoinAsync(subject, _caretakerRole);
        await Members().SetDirectPermissionsAsync(_tenant, member.Id, ["extra.read"], Now, CancellationToken.None);
        await Members().SetLimitTo24HoursAsync(_tenant, member.Id, true, Now, CancellationToken.None);
        await Subjects().GrantPlatformAdminAsync([subject], CancellationToken.None);
        var outsider = await SubjectAsync("Outsider");

        var read = await Members().ReadSubjectMembershipAsync(subject, _tenant, CancellationToken.None);
        read!.IsPlatformAdmin.Should().BeTrue();
        read.Membership.Should().BeEquivalentTo(new { Id = member.Id, Access = new { LimitTo24Hours = true } });
        read.Membership!.Access.EffectivePermissions.Should().Contain("extra.read").And.Contain(RoleSeeds.Permissions[RoleSeeds.Caretaker][0]);

        (await Members().ReadSubjectMembershipAsync(subject, _otherTenant, CancellationToken.None))!.Membership.Should().BeNull();
        (await Members().ReadSubjectMembershipAsync(outsider, _tenant, CancellationToken.None)).Should().BeEquivalentTo(
            new SubjectMembershipRead(false, null));
        (await Members().ReadSubjectMembershipAsync(Guid.CreateVersion7(), _tenant, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task ASubjectsMembershipsSpanTenantsOwnedFirst()
    {
        var subject = await SubjectAsync("Carer");
        await Members(_otherTenant).SeedRolesAsync(_otherTenant, CancellationToken.None);
        var otherOwner = (await Members(_otherTenant).FindRoleIdBySlugAsync(_otherTenant, RoleSeeds.Owner, CancellationToken.None))!.Value;
        (await Members().AddMemberAsync(new NewTenantMember(_tenant, subject, [_caretakerRole]), CancellationToken.None)).Should().BeTrue();
        (await Members().AddMemberAsync(new NewTenantMember(_otherTenant, subject, [otherOwner]), CancellationToken.None)).Should().BeTrue();

        (await Members().ListTenantIdsOfSubjectAsync(subject, CancellationToken.None)).Should().BeEquivalentTo([_tenant, _otherTenant]);
        (await Members().ListTenantsOfSubjectAsync(subject, activeOnly: false, CancellationToken.None)).Select(t => t.Slug).Should().Equal("other", "home");

        var memberships = await Members().ListMembershipsOfSubjectAsync(subject, CancellationToken.None);
        memberships.Select(m => m.Tenant.Slug).Should().BeEquivalentTo("home", "other");
        memberships.Single(m => m.Tenant.Slug == "other").Membership.IsOwner.Should().BeTrue();

        await new TenantRepository(new TestNocturneDbContextFactory(_db.CreateContext(Guid.Empty)))
            .UpdateAsync(_otherTenant, new TenantEdit(IsActive: false), CancellationToken.None);
        (await Members().ListTenantsOfSubjectAsync(subject, activeOnly: true, CancellationToken.None)).Select(t => t.Slug).Should().Equal("home");

        var removal = await Members().RemoveMembershipsOfSubjectAsync(subject, CancellationToken.None);
        removal.Removed.Should().Be(2);
        removal.TenantsLeftWithoutOwner.Should().Equal(
            [new LastOwnerTenant(_otherTenant, "other")],
            "the subject was the only owner of the tenant it owned: the removal goes ahead and names it");
        (await Members().ListTenantIdsOfSubjectAsync(subject, CancellationToken.None)).Should().BeEmpty();
        var again = await Members().RemoveMembershipsOfSubjectAsync(subject, CancellationToken.None);
        again.Removed.Should().Be(0);
        again.TenantsLeftWithoutOwner.Should().BeEmpty();
    }

    [Fact]
    public async Task TheSetupGateReadsCredentialsAndOrphansFromTheMembers()
    {
        (await Members().ReadSetupGateAsync(_tenant, CancellationToken.None)).Should().BeNull("no member yet");
        (await Members().CountSetupCredentialsAsync(_tenant, CancellationToken.None)).Should().Be(new SetupCredentialCounts(0, 0, 0));

        var orphan = await JoinAsync(await SubjectAsync("Orphan"), _ownerRole);
        (await Members().ReadSetupGateAsync(_tenant, CancellationToken.None)).Should().Be(new SetupGate(false, true));
        (await Members().ListOrphanedMembersAsync(_tenant, CancellationToken.None)).Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { SubjectId = orphan.SubjectId, Name = "Orphan" });

        var credentialed = await JoinAsync(await SubjectAsync("Credentialed"), _caretakerRole);
        AddPasskey(credentialed.SubjectId);
        (await Members().ReadSetupGateAsync(_tenant, CancellationToken.None)).Should().Be(new SetupGate(true, true));
        (await Members().CountSetupCredentialsAsync(_tenant, CancellationToken.None)).Should().Be(new SetupCredentialCounts(2, 1, 0));

        AddPasskey(orphan.SubjectId);
        (await Members().ReadSetupGateAsync(_tenant, CancellationToken.None)).Should().Be(new SetupGate(true, false));
        (await Members().ListOrphanedMembersAsync(_tenant, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task PlatformAdminsAreListedOnceInTheTenantTheyJoinedFirst()
    {
        var admin = await SubjectAsync("Admin");
        var homeless = await SubjectAsync("Homeless");
        var retired = await SubjectAsync("Retired", active: false);
        await Subjects().GrantPlatformAdminAsync([admin, homeless, retired], CancellationToken.None);
        var home = await JoinAsync(admin, _ownerRole);
        (await Members().AddMemberAsync(new NewTenantMember(_otherTenant, admin, []), CancellationToken.None)).Should().BeTrue();
        var other = (await Members().FindMemberAsync(_otherTenant, admin, CancellationToken.None))!;
        SetJoinedAt(other.Id, Now);
        SetJoinedAt(home.Id, Now.AddDays(1));
        await JoinAsync(await SubjectAsync("Nobody"), _caretakerRole);

        var directory = await Members().ListPlatformAdminsAsync(CancellationToken.None);

        directory.Memberships.Should().ContainSingle().Which.Should().BeEquivalentTo(new { SubjectId = admin, TenantId = _otherTenant, Slug = "other" });
        directory.ActivePlatformAdmins.Should().Be(2, "the deactivated admin is not counted; the homeless one is");
    }

    [Fact]
    public async Task TenantRolesAreCreatedEditedAndDeletedWithTheirAssignments()
    {
        var created = await Members().CreateRoleAsync(new NewTenantRole(_tenant, "Night Shift", "night-shift", "d", ["glucose.read"], IsSystem: false), CancellationToken.None);
        created.Should().BeEquivalentTo(new { TenantId = _tenant, Name = "Night Shift", Slug = "night-shift", Description = "d", IsSystem = false, MemberCount = 0 });
        var member = await JoinAsync(await SubjectAsync("Nurse"), created.Id);

        var updated = await Members().UpdateRoleAsync(_tenant, created.Id, new TenantRoleEdit("Nights", "nights", null, ["glucose.read", "treatments.read"]), Now, CancellationToken.None);
        updated.Should().BeEquivalentTo(new { Name = "Nights", Slug = "nights", Description = (string?)null, MemberCount = 1 });
        updated!.Permissions.Should().Equal("glucose.read", "treatments.read");
        (await Members().UpdateRoleAsync(_otherTenant, created.Id, new TenantRoleEdit("x", "x", null, []), Now, CancellationToken.None)).Should().BeNull();

        (await Members().DeleteRoleAsync(_tenant, created.Id, CancellationToken.None)).Should().BeTrue();
        (await Members().GetRoleAsync(_tenant, created.Id, CancellationToken.None)).Should().BeNull();
        (await Members().GetMemberAsync(_tenant, member.Id, CancellationToken.None))!.Roles.Should().BeEmpty();
        (await Members().DeleteRoleAsync(_tenant, created.Id, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task RequestsAreCreatedListedAndDecidedWithinTheirTenant()
    {
        var subject = await SubjectAsync("Asker");
        var id = await Members().CreateRequestAsync(new NewMembershipRequest(_tenant, subject, "please", Now), CancellationToken.None);

        (await Members().AnyPendingRequestAsync(_tenant, subject, CancellationToken.None)).Should().BeTrue();
        (await Members().AnyPendingRequestAsync(_otherTenant, subject, CancellationToken.None)).Should().BeFalse("requests are tenant-scoped");
        (await Members().FindLatestRequestAsync(_tenant, subject, CancellationToken.None)).Should().BeEquivalentTo(new
        {
            Id = id, TenantId = _tenant, SubjectId = subject, Message = "please", Status = MembershipRequestRecord.Pending, CreatedAt = Now,
        });
        (await Members().ListPendingRequestsAsync(_tenant, CancellationToken.None)).Select(r => r.Id).Should().Equal(id);
        (await Members().ListPendingRequestsAsync(_otherTenant, CancellationToken.None)).Should().BeEmpty();

        var decider = Guid.CreateVersion7();
        (await Members().DecideRequestAsync(_tenant, id,
            new MembershipRequestDecision(MembershipRequestRecord.Approved, decider, Now.AddMinutes(1), [_caretakerRole]), CancellationToken.None))
            .Should().BeTrue();
        (await Members().GetRequestAsync(_tenant, id, CancellationToken.None)).Should().BeEquivalentTo(new
        {
            Status = MembershipRequestRecord.Approved, DecidedBySubjectId = (Guid?)decider, DecidedAt = (DateTime?)Now.AddMinutes(1),
        });
        (await Members().GetRequestAsync(_tenant, id, CancellationToken.None))!.RoleIds.Should().Equal(_caretakerRole);
        (await Members().AnyPendingRequestAsync(_tenant, subject, CancellationToken.None)).Should().BeFalse();
        (await Members().GetRequestAsync(_otherTenant, id, CancellationToken.None)).Should().BeNull();
        (await Members().DecideRequestAsync(_tenant, Guid.CreateVersion7(),
            new MembershipRequestDecision(MembershipRequestRecord.Denied, decider, Now, null), CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task ARequestThatIsNoLongerPendingIsNotDecidedAgain()
    {
        var subject = await SubjectAsync("Asker");
        var id = await Members().CreateRequestAsync(new NewMembershipRequest(_tenant, subject, null, Now), CancellationToken.None);
        var decider = Guid.CreateVersion7();
        (await Members().DecideRequestAsync(_tenant, id,
            new MembershipRequestDecision(MembershipRequestRecord.Denied, decider, Now, null), CancellationToken.None)).Should().BeTrue();

        (await Members().DecideRequestAsync(_tenant, id,
            new MembershipRequestDecision(MembershipRequestRecord.Approved, decider, Now.AddMinutes(1), [_caretakerRole]), CancellationToken.None))
            .Should().BeFalse();

        (await Members().GetRequestAsync(_tenant, id, CancellationToken.None)).Should().BeEquivalentTo(new
        {
            Status = MembershipRequestRecord.Denied, DecidedAt = (DateTime?)Now,
        });
    }

    [Fact]
    public async Task EveryRequestMethodKeysOnTheTenantItIsGivenEvenOnAUnitPinnedToAnother()
    {
        var subject = await SubjectAsync("Asker");
        var id = await Members().CreateRequestAsync(new NewMembershipRequest(_tenant, subject, "please", Now), CancellationToken.None);
        await using var owned = _db.CreateContext(_tenant);
        await using var unit = DbUnitOfWork.Own(owned);
        var members = new TenantMembershipRepository(unit);

        (await members.AnyPendingRequestAsync(_otherTenant, subject, CancellationToken.None)).Should().BeFalse();
        (await members.FindLatestRequestAsync(_otherTenant, subject, CancellationToken.None)).Should().BeNull();
        (await members.ListPendingRequestsAsync(_otherTenant, CancellationToken.None)).Should().BeEmpty();
        (await members.GetRequestAsync(_otherTenant, id, CancellationToken.None)).Should().BeNull();
        (await members.DecideRequestAsync(_otherTenant, id,
            new MembershipRequestDecision(MembershipRequestRecord.Denied, subject, Now, null), CancellationToken.None)).Should().BeFalse();
        (await members.GetRequestAsync(_tenant, id, CancellationToken.None))!.Status.Should().Be(MembershipRequestRecord.Pending);
    }

    [Fact]
    public async Task AWriteOnAUnitIsStagedUntilTheOwnerSavesAndReadsItsOwnRows()
    {
        var subject = await SubjectAsync("Staged");
        await using var owned = _db.CreateContext(_tenant);
        await using var unit = DbUnitOfWork.Own(owned);
        var members = new TenantMembershipRepository(unit);

        (await members.AddMemberAsync(new NewTenantMember(_tenant, subject, [_ownerRole]), CancellationToken.None)).Should().BeTrue();
        (await Members().IsMemberAsync(_tenant, subject, CancellationToken.None)).Should().BeFalse("the unit has not been saved");

        await owned.SaveChangesAsync();
        var member = (await Members().FindMemberAsync(_tenant, subject, CancellationToken.None))!;
        member.Roles.Select(r => r.RoleId).Should().Equal(_ownerRole);

        await members.ReplaceRolesAsync(_tenant, member.Id, [_ownerRole], [_caretakerRole], Now, CancellationToken.None);
        (await Members().GetMemberAsync(_tenant, member.Id, CancellationToken.None))!.Roles.Select(r => r.RoleId).Should().Equal(new[] { _ownerRole }, "not saved yet");
        await owned.SaveChangesAsync();
        (await Members().GetMemberAsync(_tenant, member.Id, CancellationToken.None))!.Roles.Select(r => r.RoleId).Should().Equal(_caretakerRole);

        var pinned = () => members.ListTenantIdsOfSubjectAsync(subject, CancellationToken.None);
        await pinned.Should().ThrowAsync<InvalidOperationException>("a unit's context cannot take another pin");
    }
}

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// <see cref="SubjectRepository"/> over SQLite: the reads and writes a subject's life is made of,
/// the sign-in method questions that span passkeys and provider identities, and the writes staged
/// on a unit of work until its owner commits.
/// </summary>
public class SubjectRepositoryTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 31, 9, 0, 0, DateTimeKind.Utc);

    private readonly Guid _tenant = Guid.CreateVersion7();
    private readonly Guid _otherTenant = Guid.CreateVersion7();
    private readonly Guid _provider = Guid.CreateVersion7();
    private readonly Guid _disabledProvider = Guid.CreateVersion7();
    private readonly SqliteTestDatabase _db;

    public SubjectRepositoryTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(_tenant).SeedTenant(_otherTenant, "other");
        using var db = _db.CreateContext();
        db.Roles.Add(new RoleEntity { Id = Guid.CreateVersion7(), Name = "admin", Permissions = ["*"], IsSystemRole = true });
        db.Roles.Add(new RoleEntity { Id = Guid.CreateVersion7(), Name = "public", Permissions = ["glucose:read"] });
        db.OidcProviders.Add(new OidcProviderEntity
        {
            Id = _provider, Name = "Keycloak", IssuerUrl = "https://issuer.example", ClientId = "nocturne", IsEnabled = true,
        });
        db.OidcProviders.Add(new OidcProviderEntity
        {
            Id = _disabledProvider, Name = "Old", IssuerUrl = "https://old.example", ClientId = "nocturne", IsEnabled = false,
        });
        db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private SubjectRepository Subjects(Guid? tenant = null) =>
        new(new TestNocturneDbContextFactory(_db.CreateContext(tenant ?? _tenant)));

    private Task<Guid> CreateAsync(NewSubject subject) => Subjects().CreateAsync(subject, CancellationToken.None);

    private async Task<SubjectRecord> GetAsync(Guid id) => (await Subjects().GetAsync(id, CancellationToken.None))!;

    private void AddMember(Guid tenant, Guid subject)
    {
        using var db = _db.CreateContext();
        db.TenantMembers.Add(new TenantMemberEntity { Id = Guid.CreateVersion7(), TenantId = tenant, SubjectId = subject });
        db.SaveChanges();
    }

    private Guid AddPasskey(Guid subject)
    {
        using var db = _db.CreateContext();
        var passkey = new PasskeyCredentialEntity
        {
            Id = Guid.CreateVersion7(), SubjectId = subject, CredentialId = Guid.NewGuid().ToByteArray(), PublicKey = [1],
        };
        db.PasskeyCredentials.Add(passkey);
        db.SaveChanges();
        return passkey.Id;
    }

    private Task<Guid> LinkAsync(Guid subject, Guid provider, string sub, string issuer = "https://issuer.example") =>
        Subjects().AddOidcIdentityAsync(new NewSubjectOidcIdentity(subject, provider, sub, issuer, "a@b.example", Now), CancellationToken.None);

    [Fact]
    public async Task ACreatedSubjectReadsBackWithItsRolesAndTheIdItAsked()
    {
        var wanted = Guid.CreateVersion7();
        var id = await CreateAsync(new NewSubject(
            "Rhys", Username: "rhys", Email: "r@x.example", Notes: "n", Id: wanted,
            RoleNames: [" admin", "ADMIN", "public", "missing"]));

        id.Should().Be(wanted);
        var subject = await GetAsync(id);
        subject.Should().BeEquivalentTo(new
        {
            Name = "Rhys", Username = "rhys", Email = "r@x.example", Notes = "n", IsActive = true,
            IsSystemSubject = false, IsDemoSubject = false, ApprovalStatus = "Approved",
        });
        subject.Roles.Select(r => r.Name).Should().BeEquivalentTo("admin", "public");
        subject.Roles.Single(r => r.Name == "admin").Permissions.Should().Equal("*");
        (await Subjects().GetRoleNamesAsync(id, CancellationToken.None)).Should().BeEquivalentTo("admin", "public");
        (await Subjects().ExistsAsync(id, CancellationToken.None)).Should().BeTrue();
        (await Subjects().ExistsAsync(Guid.CreateVersion7(), CancellationToken.None)).Should().BeFalse();
        (await Subjects().GetAsync(Guid.CreateVersion7(), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task TheBatchReadsSkipRolesAndUnknownIds()
    {
        var a = await CreateAsync(new NewSubject("A", RoleNames: ["admin"]));
        var b = await CreateAsync(new NewSubject("B"));

        var many = await Subjects().GetManyAsync([a, b, Guid.CreateVersion7()], CancellationToken.None);
        many.Select(s => s.Name).Should().BeEquivalentTo("A", "B");
        many.Should().OnlyContain(s => s.Roles.Count == 0, "a list read leaves roles unread");
        (await Subjects().GetNamesAsync([a, Guid.CreateVersion7()], CancellationToken.None))
            .Should().BeEquivalentTo(new Dictionary<Guid, string> { [a] = "A" });
        (await Subjects().GetManyAsync([], CancellationToken.None)).Should().BeEmpty();
        (await Subjects().GetPreferencesAsync(a, CancellationToken.None)).Should().Be(new SubjectPreferences(null, null));
        (await Subjects().GetPreferencesAsync(Guid.CreateVersion7(), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task TheSystemAndMemberLookupsReadWhatTheirCallersAsk()
    {
        var system = await CreateAsync(new NewSubject("Public", IsSystemSubject: true, RoleNames: ["public"]));
        await CreateAsync(new NewSubject("Public"));
        var active = await CreateAsync(new NewSubject("Active", Username: "sam"));
        var inactive = await CreateAsync(new NewSubject("Inactive", Username: "pat", IsActive: false));
        AddMember(_tenant, active);
        AddMember(_tenant, inactive);

        var found = await Subjects().FindSystemSubjectAsync("Public", CancellationToken.None);
        found!.Id.Should().Be(system);
        found.Roles.Select(r => r.Name).Should().Equal("public");
        (await Subjects().FindSystemSubjectAsync("Nobody", CancellationToken.None)).Should().BeNull();

        (await Subjects().FindMemberByUsernameAsync(_tenant, "sam", activeOnly: true, CancellationToken.None))!.Id.Should().Be(active);
        (await Subjects().FindMemberByUsernameAsync(_tenant, "pat", activeOnly: true, CancellationToken.None)).Should().BeNull();
        (await Subjects().FindMemberByUsernameAsync(_tenant, "pat", activeOnly: false, CancellationToken.None))!.Id.Should().Be(inactive);
        (await Subjects().FindMemberByUsernameAsync(_otherTenant, "sam", activeOnly: false, CancellationToken.None))
            .Should().BeNull("the username is resolved through the tenant's memberships");
    }

    [Fact]
    public async Task TheSetupOwnerCandidateIsTheOldestActivePersonAndNeverTheDemoVisitor()
    {
        await CreateAsync(new NewSubject("Demo", IsDemoSubject: true));
        await CreateAsync(new NewSubject("System", IsSystemSubject: true));
        await CreateAsync(new NewSubject("Gone", IsActive: false));
        var owner = await CreateAsync(new NewSubject("Owner"));
        await CreateAsync(new NewSubject("Later"));

        (await Subjects().FindSetupOwnerCandidateAsync(_tenant, CancellationToken.None))!.Id.Should().Be(owner);
    }

    [Fact]
    public async Task AccessRequestsAreListedNewestFirstAndEnrolmentCandidatesHoldNoFactor()
    {
        var older = await CreateAsync(new NewSubject("Ann", IsActive: false, ApprovalStatus: "Pending", AccessRequestMessage: "hi"));
        var newer = await CreateAsync(new NewSubject("Ann", IsActive: false, ApprovalStatus: "Pending"));
        await CreateAsync(new NewSubject("Bot", IsSystemSubject: true, IsActive: false, ApprovalStatus: "Pending"));
        var withPasskey = await CreateAsync(new NewSubject("Ann", IsActive: false, ApprovalStatus: "Pending"));
        AddPasskey(withPasskey);
        var invitee = await CreateAsync(new NewSubject("Inv", Username: "inv"));
        var linked = await CreateAsync(new NewSubject("Inv2", Username: "inv"));
        await LinkAsync(linked, _provider, "ext");
        var linkedToDisabled = await CreateAsync(new NewSubject("Inv3", Username: "inv"));
        await LinkAsync(linkedToDisabled, _disabledProvider, "ext2", "https://old.example");

        var pending = await Subjects().ListPendingAccessRequestsAsync(CancellationToken.None);
        pending.Select(s => s.Id).Should().Equal(withPasskey, newer, older);
        pending.Should().OnlyContain(s => s.Roles.Count == 0);
        (await Subjects().AnyPendingAccessRequestNamedAsync("Ann", CancellationToken.None)).Should().BeTrue();
        (await Subjects().AnyPendingAccessRequestNamedAsync("Zed", CancellationToken.None)).Should().BeFalse();

        (await Subjects().FindCredentiallessSubjectIdsAsync(new EnrolmentMatch.AccessRequest("Ann"), CancellationToken.None))
            .Should().Equal(newer, older);
        (await Subjects().FindCredentiallessSubjectIdsAsync(new EnrolmentMatch.Invite("inv"), CancellationToken.None))
            .Should().Equal(linkedToDisabled, invitee);
    }

    [Fact]
    public async Task TheEditsChangeOnlyWhatTheyName()
    {
        var id = await CreateAsync(new NewSubject("Old", Username: "old", Email: "old@x.example", Notes: "keep"));

        (await Subjects().UpdateAsync(id, new SubjectEdit(Name: "New", IsPlatformAdmin: true, LastLoginAt: Now), CancellationToken.None))
            .Should().BeTrue();
        var edited = await GetAsync(id);
        edited.Should().BeEquivalentTo(new
        {
            Name = "New", Username = "old", Email = "old@x.example", Notes = "keep", IsPlatformAdmin = true, LastLoginAt = Now,
        });

        await Subjects().UpdateAsync(id, new SubjectEdit(PreferredLanguage: "fr", Preferences: "{}", ApprovalStatus: "Pending", IsActive: false), CancellationToken.None);
        (await Subjects().GetPreferencesAsync(id, CancellationToken.None)).Should().Be(new SubjectPreferences("fr", "{}"));
        (await GetAsync(id)).Should().BeEquivalentTo(new { ApprovalStatus = "Pending", IsActive = false });

        var profile = await Subjects().UpdateProfileAsync(id, new SubjectProfile("P", null, null, true), CancellationToken.None);
        profile.Should().BeEquivalentTo(new { Name = "P", Email = (string?)null, Notes = (string?)null, IsActive = true });

        (await Subjects().SetAccessRequestMessageAsync(id, "please", CancellationToken.None)).Should().BeTrue();
        (await GetAsync(id)).AccessRequestMessage.Should().Be("please");
        await Subjects().SetAccessRequestMessageAsync(id, null, CancellationToken.None);
        (await GetAsync(id)).AccessRequestMessage.Should().BeNull();

        (await Subjects().UpdateAsync(Guid.CreateVersion7(), new SubjectEdit(Name: "x"), CancellationToken.None)).Should().BeFalse();
        (await Subjects().UpdateProfileAsync(Guid.CreateVersion7(), new SubjectProfile("x", null, null, true), CancellationToken.None)).Should().BeNull();
        (await Subjects().SetAccessRequestMessageAsync(Guid.CreateVersion7(), null, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task ThePlatformAdminFlagIsReadCountedAndGranted()
    {
        var a = await CreateAsync(new NewSubject("A"));
        var b = await CreateAsync(new NewSubject("B"));
        var demo = await CreateAsync(new NewSubject("D", IsDemoSubject: true));

        (await Subjects().AnyPlatformAdminAsync(CancellationToken.None)).Should().BeFalse();
        (await Subjects().GrantPlatformAdminAsync([a, b, Guid.CreateVersion7()], CancellationToken.None)).Should().Be(2);
        (await Subjects().GrantPlatformAdminAsync([], CancellationToken.None)).Should().Be(0);
        (await Subjects().AnyPlatformAdminAsync(CancellationToken.None)).Should().BeTrue();
        (await Subjects().CountPlatformAdminsAsync(CancellationToken.None)).Should().Be(2);
        (await Subjects().IsPlatformAdminAsync(a, CancellationToken.None)).Should().BeTrue();
        (await Subjects().IsPlatformAdminAsync(demo, CancellationToken.None)).Should().BeFalse();
        (await Subjects().IsPlatformAdminAsync(Guid.CreateVersion7(), CancellationToken.None)).Should().BeFalse();
        (await Subjects().IsDemoSubjectAsync(demo, CancellationToken.None)).Should().BeTrue();
        (await Subjects().IsDemoSubjectAsync(a, CancellationToken.None)).Should().BeFalse();
        (await Subjects().IsDemoSubjectAsync(Guid.CreateVersion7(), CancellationToken.None)).Should().BeNull("a missing row is not 'not a demo subject'");
        (await Subjects().GetSignInStateAsync(a, CancellationToken.None)).Should().Be(new SubjectSignInState(true, true));
        (await Subjects().GetSignInStateAsync(Guid.CreateVersion7(), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task ADeleteScrubsTheSubjectsPersonalDataFromTheAuditTrailAndKeepsAnotherActors()
    {
        var deleted = await CreateAsync(new NewSubject("Gone"));
        var other = await CreateAsync(new NewSubject("Other"));
        Guid ownRow, aboutRow, othersRow;
        using (var db = _db.CreateContext())
        {
            ownRow = Guid.CreateVersion7();
            aboutRow = Guid.CreateVersion7();
            othersRow = Guid.CreateVersion7();
            db.AuthAuditLog.AddRange(
                new AuthAuditLogEntity { Id = ownRow, EventType = "login", ActorSubjectId = deleted, TargetSubjectId = deleted, IpAddress = "1.1.1.1", UserAgent = "ua", DetailsJson = "{}" },
                new AuthAuditLogEntity { Id = aboutRow, EventType = "role", ActorSubjectId = other, TargetSubjectId = deleted, IpAddress = "2.2.2.2", UserAgent = "ua2", DetailsJson = "{}" },
                new AuthAuditLogEntity { Id = othersRow, EventType = "login", ActorSubjectId = other, TargetSubjectId = other, IpAddress = "3.3.3.3", UserAgent = "ua3", DetailsJson = "{}" });
            db.SaveChanges();
        }

        (await Subjects().DeleteAsync(deleted, CancellationToken.None)).Should().BeTrue();
        (await Subjects().DeleteAsync(deleted, CancellationToken.None)).Should().BeFalse();

        await using var check = _db.CreateContext();
        var rows = await check.AuthAuditLog.ToDictionaryAsync(a => a.Id);
        rows[ownRow].Should().BeEquivalentTo(new { IpAddress = (string?)null, UserAgent = (string?)null, DetailsJson = (string?)null });
        rows[aboutRow].Should().BeEquivalentTo(new { IpAddress = "2.2.2.2", UserAgent = "ua2", DetailsJson = (string?)null },
            "the address is the other actor's; the details are about the deleted subject");
        rows[othersRow].Should().BeEquivalentTo(new { IpAddress = "3.3.3.3", UserAgent = "ua3", DetailsJson = "{}" });
        (await check.Subjects.AnyAsync(s => s.Id == deleted)).Should().BeFalse();
    }

    [Fact]
    public async Task RolesAreAssignedOnceAndRemovedByName()
    {
        var id = await CreateAsync(new NewSubject("R"));
        var by = await CreateAsync(new NewSubject("Admin"));

        (await Subjects().AssignRoleAsync(id, "admin", by, Now, CancellationToken.None)).Should().Be(RoleAssignment.Assigned);
        (await Subjects().AssignRoleAsync(id, "admin", null, Now, CancellationToken.None)).Should().Be(RoleAssignment.AlreadyAssigned);
        (await Subjects().AssignRoleAsync(id, "nope", null, Now, CancellationToken.None)).Should().Be(RoleAssignment.RoleNotFound);
        (await Subjects().GetRolesAsync(id, CancellationToken.None)).Select(r => r.Name).Should().Equal("admin");

        await using (var check = _db.CreateContext())
        {
            var assignment = await check.SubjectRoles.SingleAsync(sr => sr.SubjectId == id);
            assignment.AssignedById.Should().Be(by);
            assignment.AssignedAt.Should().Be(Now);
        }

        (await Subjects().RemoveRoleAsync(id, "admin", CancellationToken.None)).Should().BeTrue();
        (await Subjects().RemoveRoleAsync(id, "admin", CancellationToken.None)).Should().BeFalse();
        (await Subjects().GetRoleNamesAsync(id, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task AnOidcIdentityLinksTouchesAndIsFoundWithItsSubject()
    {
        var id = await CreateAsync(new NewSubject("O", RoleNames: ["admin"]));
        var first = await LinkAsync(id, _provider, "ext-1");
        var second = await LinkAsync(id, _disabledProvider, "ext-2", "https://old.example");

        var match = await Subjects().FindByOidcIdentityAsync("ext-1", "https://issuer.example", CancellationToken.None);
        match!.Identity.Should().BeEquivalentTo(new { Id = first, SubjectId = id, ProviderName = "Keycloak", Email = "a@b.example" });
        match.Subject.Roles.Select(r => r.Name).Should().Equal("admin");
        (await Subjects().FindByOidcIdentityAsync("ext-1", "https://elsewhere.example", CancellationToken.None)).Should().BeNull();

        (await Subjects().TouchOidcIdentityAsync(second, Now.AddHours(1), "new@b.example", CancellationToken.None)).Should().BeTrue();
        (await Subjects().TouchOidcIdentityAsync(Guid.CreateVersion7(), Now, null, CancellationToken.None)).Should().BeFalse();

        var identities = await Subjects().GetOidcIdentitiesAsync(id, CancellationToken.None);
        identities.Select(i => i.Id).Should().Equal(first, second);
        identities[1].Should().BeEquivalentTo(new { LastUsedAt = Now.AddHours(1), Email = "new@b.example", ProviderName = "Old" });
        (await Subjects().GetMostRecentlyUsedOidcIdentityAsync(id, CancellationToken.None))!.Id.Should().Be(second);
        (await Subjects().GetMostRecentlyUsedOidcIdentityAsync(Guid.CreateVersion7(), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task TheLastWorkingFactorCannotBeRemovedAndADisabledProviderDoesNotCount()
    {
        var id = await CreateAsync(new NewSubject("F"));
        var passkey = AddPasskey(id);
        var working = await LinkAsync(id, _provider, "ext-1");
        var dead = await LinkAsync(id, _disabledProvider, "ext-2", "https://old.example");

        (await Subjects().CountPrimaryAuthFactorsAsync(id, CancellationToken.None)).Should().Be(2);
        (await Subjects().TryRemoveFactorAsync(id, new SignInFactor.OidcIdentity(dead), CancellationToken.None))
            .Should().Be(FactorRemovalResult.Removed);
        (await Subjects().TryRemoveFactorAsync(id, new SignInFactor.Passkey(passkey), CancellationToken.None))
            .Should().Be(FactorRemovalResult.Removed);
        (await Subjects().TryRemoveFactorAsync(id, new SignInFactor.OidcIdentity(working), CancellationToken.None))
            .Should().Be(FactorRemovalResult.LastPrimaryFactor);
        (await Subjects().TryRemoveFactorAsync(id, new SignInFactor.Passkey(passkey), CancellationToken.None))
            .Should().Be(FactorRemovalResult.NotFound);
        (await Subjects().TryRemoveFactorAsync(Guid.CreateVersion7(), new SignInFactor.OidcIdentity(working), CancellationToken.None))
            .Should().Be(FactorRemovalResult.NotFound, "a factor is removed by its holder only");
        (await Subjects().CountPrimaryAuthFactorsAsync(id, CancellationToken.None)).Should().Be(1);
    }

    [Fact]
    public async Task TheTenantQuestionsSeeMembersCredentialsAndOrphansAndAProvidersStrandedSubjects()
    {
        var credentialed = await CreateAsync(new NewSubject("C"));
        var orphan = await CreateAsync(new NewSubject("O"));
        var demo = await CreateAsync(new NewSubject("D", IsDemoSubject: true));
        AddMember(_tenant, credentialed);
        AddMember(_tenant, orphan);
        AddMember(_tenant, demo);
        AddMember(_otherTenant, demo);
        await LinkAsync(credentialed, _provider, "ext-1");
        var alsoPasskey = await CreateAsync(new NewSubject("P"));
        AddPasskey(alsoPasskey);
        await LinkAsync(alsoPasskey, _provider, "ext-2");

        (await Subjects().TenantHasCredentialedMemberAsync(_tenant, CancellationToken.None)).Should().BeTrue();
        (await Subjects().TenantHasCredentialedMemberAsync(_otherTenant, CancellationToken.None)).Should().BeFalse("the demo visitor holds nothing");
        (await Subjects().HasOrphanedMemberAsync(_tenant, CancellationToken.None)).Should().BeTrue();
        (await Subjects().HasOrphanedMemberAsync(_otherTenant, CancellationToken.None)).Should().BeFalse("a demo visitor is no orphan");
        (await Subjects().CountStrandedByProviderAsync(_provider, CancellationToken.None))
            .Should().Be(1, "the subject with a passkey too is not stranded");
        (await Subjects().CountStrandedByProviderAsync(_disabledProvider, CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task WritesOnAUnitStayStagedUntilItsOwnerCommits()
    {
        Guid created;
        await using (var unit = DbUnitOfWork.Own(_db.CreateContext()))
        {
            var staged = new SubjectRepository(unit);
            created = await staged.CreateAsync(new NewSubject("Staged", RoleNames: ["admin"]), CancellationToken.None);
            await staged.AddOidcIdentityAsync(
                new NewSubjectOidcIdentity(created, _provider, "ext", "https://issuer.example", null, Now), CancellationToken.None);

            (await Subjects().GetAsync(created, CancellationToken.None)).Should().BeNull("nothing is saved until the unit's owner commits");
            (await staged.FindByOidcIdentityAsync("ext", "https://issuer.example", CancellationToken.None))
                .Should().BeNull("the staged rows are not yet in the store");

            await unit.CommitAsync(token => unit.Context.SaveChangesAsync(token), CancellationToken.None);
        }

        var subject = await GetAsync(created);
        subject.Roles.Select(r => r.Name).Should().Equal("admin");
        (await Subjects().FindByOidcIdentityAsync("ext", "https://issuer.example", CancellationToken.None))!.Subject.Id.Should().Be(created);

        await using var pinned = DbUnitOfWork.Own(_db.CreateContext());
        var cannotPin = () => new SubjectRepository(pinned).IsDemoSubjectAsync(created, CancellationToken.None);
        await cannotPin.Should().ThrowAsync<InvalidOperationException>("a unit runs on its own context and pins no other");
    }
}

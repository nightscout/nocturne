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

    /// <summary>An id that sorts after every <see cref="Ordered"/> id with a smaller <paramref name="n"/>, on SQLite and in memory alike.</summary>
    private static Guid Ordered(int n) => Guid.Parse($"00000000-0000-7000-8000-{n:D12}");

    /// <summary>The context stamps <c>created_at</c> on add from its own clock; a later save leaves it alone.</summary>
    private void SetCreatedAt(Guid subject, DateTime createdAt)
    {
        using var db = _db.CreateContext();
        db.Subjects.Single(s => s.Id == subject).CreatedAt = createdAt;
        db.SaveChanges();
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
        (await Subjects().GetRolesAsync(id, CancellationToken.None)).Select(r => r.Name).Should().BeEquivalentTo("admin", "public");
        (await Subjects().GetAsync(Guid.CreateVersion7(), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task ASubjectCreatedFromAnOidcIdentityIsOneSaveWithIt()
    {
        var link = new NewOidcLink(_provider, "ext-first", "https://issuer.example", "f@x.example", Now);
        var id = await CreateAsync(new NewSubject("First", Email: "f@x.example", RoleNames: ["admin"], OidcIdentity: link));

        var match = await Subjects().FindByOidcIdentityAsync("ext-first", "https://issuer.example", CancellationToken.None);
        match!.Subject.Id.Should().Be(id);
        match.Subject.Roles.Select(r => r.Name).Should().Equal("admin");
        match.Identity.Should().BeEquivalentTo(new { SubjectId = id, ProviderId = _provider, Email = "f@x.example", LinkedAt = Now });

        // The race: a second first sign-in on the same identity. The unique (sub, issuer) index
        // refuses the identity, and the subject must fail with it rather than stay as a shell.
        var again = Guid.CreateVersion7();
        var clash = () => CreateAsync(new NewSubject("Again", Email: "f@x.example", Id: again, OidcIdentity: link));
        await clash.Should().ThrowAsync<DbUpdateException>();
        (await Subjects().GetAsync(again, CancellationToken.None)).Should().BeNull("the loser's subject is not stored without its identity");
        (await Subjects().FindByOidcIdentityAsync("ext-first", "https://issuer.example", CancellationToken.None))!.Subject.Id.Should().Be(id);
        await using var check = _db.CreateContext();
        (await check.SubjectOidcIdentities.CountAsync(i => i.OidcSubjectId == "ext-first")).Should().Be(1);
    }

    [Fact]
    public async Task TheBatchReadsSkipRolesAndUnknownIds()
    {
        var a = await CreateAsync(new NewSubject("A", RoleNames: ["admin"]));
        var b = await CreateAsync(new NewSubject("B"));

        var many = await Subjects().GetManyAsync([a, b, Guid.CreateVersion7()], CancellationToken.None);
        many.Select(s => s.Name).Should().BeEquivalentTo("A", "B");
        many.Should().OnlyContain(s => s.Roles.Count == 0, "a list read leaves roles unread");
        (await Subjects().GetManyAsync([], CancellationToken.None)).Should().BeEmpty();
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
        // Explicit ids: the candidate is the first by id, and v7 ids minted in one millisecond do not order.
        await CreateAsync(new NewSubject("Demo", IsDemoSubject: true, Id: Ordered(1)));
        await CreateAsync(new NewSubject("System", IsSystemSubject: true, Id: Ordered(2)));
        await CreateAsync(new NewSubject("Gone", IsActive: false, Id: Ordered(3)));
        var owner = await CreateAsync(new NewSubject("Owner", Id: Ordered(4)));
        await CreateAsync(new NewSubject("Later", Id: Ordered(5)));

        (await Subjects().FindSetupOwnerCandidateAsync(_tenant, CancellationToken.None))!.Id.Should().Be(owner);
    }

    [Fact]
    public async Task AccessRequestsAreListedNewestFirstAndEnrolmentCandidatesHoldNoFactor()
    {
        // Explicit ids: the candidate order is by id, and v7 ids minted in one millisecond do not order.
        var older = await CreateAsync(new NewSubject("Ann", IsActive: false, ApprovalStatus: "Pending", AccessRequestMessage: "hi", Id: Ordered(1)));
        var newer = await CreateAsync(new NewSubject("Ann", IsActive: false, ApprovalStatus: "Pending", Id: Ordered(2)));
        await CreateAsync(new NewSubject("Bot", IsSystemSubject: true, IsActive: false, ApprovalStatus: "Pending", Id: Ordered(3)));
        var withPasskey = await CreateAsync(new NewSubject("Ann", IsActive: false, ApprovalStatus: "Pending", Id: Ordered(4)));
        AddPasskey(withPasskey);
        var invitee = await CreateAsync(new NewSubject("Inv", Username: "inv", Id: Ordered(5)));
        var linked = await CreateAsync(new NewSubject("Inv2", Username: "inv", Id: Ordered(6)));
        await LinkAsync(linked, _provider, "ext");
        var linkedToDisabled = await CreateAsync(new NewSubject("Inv3", Username: "inv", Id: Ordered(7)));
        await LinkAsync(linkedToDisabled, _disabledProvider, "ext2", "https://old.example");

        // The pending list orders by CreatedAt, which the context stamps at save; spread them out.
        SetCreatedAt(older, Now.AddMinutes(-2));
        SetCreatedAt(newer, Now.AddMinutes(-1));
        SetCreatedAt(withPasskey, Now);

        var pending = await Subjects().ListPendingAccessRequestsAsync(CancellationToken.None);
        pending.Select(s => s.Id).Should().Equal(withPasskey, newer, older);
        pending.Should().OnlyContain(s => s.Roles.Count == 0);
        pending.Should().Contain(s => s.Name == "Ann").And.NotContain(s => s.Name == "Bot");

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
            .Should().BeOfType<SubjectChange.Changed>();
        var edited = await GetAsync(id);
        edited.Should().BeEquivalentTo(new
        {
            Name = "New", Username = "old", Email = "old@x.example", Notes = "keep", IsPlatformAdmin = true, LastLoginAt = Now,
        });

        await Subjects().UpdateAsync(id, new SubjectEdit(PreferredLanguage: "fr", Preferences: "{}", ApprovalStatus: "Pending", IsActive: false), CancellationToken.None);
        (await GetAsync(id)).Should().BeEquivalentTo(new { PreferredLanguage = "fr", Preferences = "{}" });
        (await GetAsync(id)).Should().BeEquivalentTo(new { ApprovalStatus = "Pending", IsActive = false });

        var profile = await Subjects().UpdateProfileAsync(id, new SubjectProfile("P", null, null), CancellationToken.None);
        profile.Should().BeEquivalentTo(new { Name = "P", Email = (string?)null, Notes = (string?)null, IsActive = false },
            "a profile edit does not change whether the subject is active");

        (await Subjects().SetAccessRequestMessageAsync(id, "please", CancellationToken.None)).Should().BeTrue();
        (await GetAsync(id)).AccessRequestMessage.Should().Be("please");
        await Subjects().SetAccessRequestMessageAsync(id, null, CancellationToken.None);
        (await GetAsync(id)).AccessRequestMessage.Should().BeNull();

        (await Subjects().UpdateAsync(Guid.CreateVersion7(), new SubjectEdit(Name: "x"), CancellationToken.None)).Should().BeOfType<SubjectChange.NotFound>();
        (await Subjects().UpdateAsync(Guid.CreateVersion7(), new SubjectEdit(IsActive: false), CancellationToken.None)).Should().BeOfType<SubjectChange.NotFound>();
        (await Subjects().UpdateProfileAsync(Guid.CreateVersion7(), new SubjectProfile("x", null, null), CancellationToken.None)).Should().BeNull();
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

        (await Subjects().DeleteAsync(deleted, CancellationToken.None)).Should().BeOfType<SubjectChange.Changed>();
        (await Subjects().DeleteAsync(deleted, CancellationToken.None)).Should().BeOfType<SubjectChange.NotFound>();

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
        (await Subjects().GetRolesAsync(id, CancellationToken.None)).Should().BeEmpty();
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
        var cannotDelete = () => new SubjectRepository(pinned).DeleteAsync(created, CancellationToken.None);
        await cannotDelete.Should().ThrowAsync<InvalidOperationException>("a unit's owner commits; the delete's transaction is its own");
        var cannotRemove = () => new SubjectRepository(pinned).TryRemoveFactorAsync(created, new SignInFactor.Passkey(Guid.NewGuid()), CancellationToken.None);
        await cannotRemove.Should().ThrowAsync<InvalidOperationException>();
    }
}

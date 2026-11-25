using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
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
        new(new TestNocturneDbContextFactory(_db.CreateContext(tenant ?? _tenant)), AccountLinkRoutes.Off);

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
    public async Task RolesThatDifferOnlyByCaseAreTwoRolesAndASubjectGivenBothHoldsBoth()
    {
        using (var db = _db.CreateContext())
        {
            db.Roles.Add(new RoleEntity { Id = Guid.CreateVersion7(), Name = "Admin", Permissions = ["treatments:read"] });
            db.SaveChanges();
        }

        var both = await CreateAsync(new NewSubject("Both", RoleNames: ["Admin", "admin"]));
        var upper = await CreateAsync(new NewSubject("Upper", RoleNames: ["Admin"]));
        var lower = await CreateAsync(new NewSubject("Lower", RoleNames: ["admin"]));
        var neither = await CreateAsync(new NewSubject("Neither", RoleNames: ["ADMIN"]));

        (await GetAsync(both)).Roles.Select(r => r.Name).Should().BeEquivalentTo("Admin", "admin");
        (await GetAsync(upper)).Roles.Select(r => r.Name).Should().Equal("Admin");
        (await GetAsync(lower)).Roles.Select(r => r.Name).Should().Equal("admin");
        (await GetAsync(neither)).Roles.Should().BeEmpty();
    }

    [Fact]
    public async Task ACreatedSubjectReadsBackWithItsRolesAndTheIdItAsked()
    {
        var wanted = Guid.CreateVersion7();
        var id = await CreateAsync(new NewSubject(
            "Rhys", Username: "rhys", Email: "r@x.example", Notes: "n", Id: wanted,
            RoleNames: [" admin", "admin", "public", "missing"]));

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

        (await Subjects().FindMemberByUsernameAsync(_tenant, "sam", activeOnly: true, Now, CancellationToken.None))!.Id.Should().Be(active);
        (await Subjects().FindMemberByUsernameAsync(_tenant, "pat", activeOnly: true, Now, CancellationToken.None)).Should().BeNull();
        (await Subjects().FindMemberByUsernameAsync(_tenant, "pat", activeOnly: false, Now, CancellationToken.None))!.Id.Should().Be(inactive);
        (await Subjects().FindMemberByUsernameAsync(_otherTenant, "sam", activeOnly: false, Now, CancellationToken.None))
            .Should().BeNull("the username is resolved through the tenant's memberships");
    }

    [Fact]
    public async Task TheMembersByConfirmedEmailAreTheSitesActivePeopleWhoseConfirmedAddressIsTheOneNamed()
    {
        async Task<Guid> Member(NewSubject subject, Guid? tenant = null)
        {
            var id = await CreateAsync(subject);
            AddMember(tenant ?? _tenant, id);
            return id;
        }

        var parent = await Member(new NewSubject("Parent", Email: "a@example.test", EmailConfirmedAt: Now));
        var child = await Member(new NewSubject("Child", Email: " A@Example.TEST ", EmailConfirmedAt: Now));
        await Member(new NewSubject("Unconfirmed", Email: "a@example.test"));
        await Member(new NewSubject("Gone", Email: "a@example.test", IsActive: false, EmailConfirmedAt: Now));
        await Member(new NewSubject("System", Email: "a@example.test", IsSystemSubject: true, EmailConfirmedAt: Now));
        await Member(new NewSubject("Demo", Email: "a@example.test", IsDemoSubject: true, EmailConfirmedAt: Now));
        await Member(new NewSubject("Elsewhere", Email: "a@example.test", EmailConfirmedAt: Now), _otherTenant);
        await CreateAsync(new NewSubject("No membership", Email: "a@example.test", EmailConfirmedAt: Now));
        await Member(new NewSubject("Lookalike", Email: "aa@example.test", EmailConfirmedAt: Now));

        (await Subjects().FindMembersByConfirmedEmailAsync(_tenant, "a@example.test", Now, CancellationToken.None))
            .Select(s => s.Id).Should().BeEquivalentTo([parent, child]);
    }

    [Fact]
    public async Task AnAddressDifferingByAKelvinSign_IsNotAMembersConfirmedAddress()
    {
        var kate = await CreateAsync(new NewSubject("Kate", Email: "kate@example.test", EmailConfirmedAt: Now));
        AddMember(_tenant, kate);

        (await Subjects().FindMembersByConfirmedEmailAsync(_tenant, "Kate@example.test", Now, CancellationToken.None))
            .Should().BeEmpty();
        (await Subjects().FindMembersByConfirmedEmailAsync(_tenant, "KATE@example.test", Now, CancellationToken.None))
            .Select(s => s.Id).Should().Equal(kate);
    }

    /// <summary>A membership of <paramref name="tenant"/> holding the role <paramref name="slug"/>, created for the tenant on first use.</summary>
    private void AddMemberWithRole(Guid tenant, Guid subject, string slug)
    {
        using var db = _db.CreateContext();
        var role = db.TenantRoles.SingleOrDefault(r => r.TenantId == tenant && r.Slug == slug);
        if (role is null)
        {
            role = new TenantRoleEntity { Id = Guid.CreateVersion7(), TenantId = tenant, Name = slug, Slug = slug };
            db.TenantRoles.Add(role);
        }

        var member = new TenantMemberEntity { Id = Guid.CreateVersion7(), TenantId = tenant, SubjectId = subject };
        db.TenantMembers.Add(member);
        db.TenantMemberRoles.Add(new TenantMemberRoleEntity { Id = Guid.CreateVersion7(), TenantMemberId = member.Id, TenantRoleId = role.Id });
        db.SaveChanges();
    }

    /// <summary>
    /// What the setup owner step leaves for <paramref name="tenant"/>: the subject, recorded for the
    /// tenant, with the owner membership and the global admin role.
    /// </summary>
    private async Task<Guid> CreateSetupOwnerAsync(NewSubject subject, Guid? tenant = null)
    {
        var id = await Subjects().CreateSetupOwnerAsync(
            tenant ?? _tenant, subject with { RoleNames = ["admin"] }, CancellationToken.None);
        AddMemberWithRole(tenant ?? _tenant, id, RoleSeeds.Owner);
        return id;
    }

    /// <summary>A subject of the same shape as <see cref="CreateSetupOwnerAsync"/>'s that no owner step recorded.</summary>
    private async Task<Guid> CreateUnrecordedOwnerAsync(NewSubject subject)
    {
        var id = await CreateAsync(subject with { RoleNames = ["admin"] });
        AddMemberWithRole(_tenant, id, RoleSeeds.Owner);
        return id;
    }

    private async Task<Guid?> CandidateAsync(Guid? tenant = null, bool linkRoute = false) =>
        (await new SubjectRepository(
                new TestNocturneDbContextFactory(_db.CreateContext(tenant ?? _tenant)), AccountLinkRoutes.Of(linkRoute))
            .FindSetupOwnerCandidateAsync(tenant ?? _tenant, CancellationToken.None))?.Id;

    private void AddRefreshToken(Guid subject, DateTime expiresAt)
    {
        using var db = _db.CreateContext();
        db.RefreshTokens.Add(new RefreshTokenEntity
        {
            Id = Guid.CreateVersion7(), TokenHash = Guid.NewGuid().ToString("N"), SubjectId = subject,
            IssuedAt = expiresAt.AddDays(-7), ExpiresAt = expiresAt,
        });
        db.SaveChanges();
    }

    private void AddLoginCode(Guid subject, LoginCodePurpose purpose, DateTime expiresAt, DateTime? consumedAt = null)
    {
        using var db = _db.CreateContext(_tenant);
        db.LoginCodes.Add(new LoginCodeEntity
        {
            Id = Guid.CreateVersion7(), TenantId = _tenant, SubjectId = subject, CodeHash = Guid.NewGuid().ToString("N"),
            Purpose = purpose, ExpiresAt = expiresAt, ConsumedAt = consumedAt,
        });
        db.SaveChanges();
    }

    private void EditSubject(Guid subject, Action<SubjectEntity> edit)
    {
        using var db = _db.CreateContext();
        edit(db.Subjects.Single(s => s.Id == subject));
        db.SaveChanges();
    }

    private Guid? RecordOf(Guid tenant)
    {
        using var db = _db.CreateContext();
        return db.Tenants.Single(t => t.Id == tenant).SetupOwnerSubjectId;
    }

    [Fact]
    public async Task TheSetupOwnerCandidateIsTheSubjectTheOwnerStepRecordedForTheTenant()
    {
        await CreateUnrecordedOwnerAsync(new NewSubject("Unrecorded", Id: Ordered(1)));
        (await CandidateAsync()).Should().BeNull("a factor-less owner no owner step recorded is not resumed");

        var first = await CreateSetupOwnerAsync(new NewSubject("First", Id: Ordered(2)));
        (await CandidateAsync()).Should().Be(first);

        var second = await CreateSetupOwnerAsync(new NewSubject("Second", Id: Ordered(3)));
        (await CandidateAsync()).Should().Be(second, "a new record replaces the earlier one");

        var elsewhere = await CreateSetupOwnerAsync(new NewSubject("Elsewhere", Id: Ordered(4)), _otherTenant);
        (await CandidateAsync(_otherTenant)).Should().Be(elsewhere);
        (await CandidateAsync()).Should().Be(second, "each tenant has its own record");
    }

    [Fact]
    public async Task CreatingTheSetupOwnerRecordsItInTheSameSave()
    {
        Guid created;
        await using (var unit = DbUnitOfWork.Own(_db.CreateContext()))
        {
            created = await new SubjectRepository(unit, AccountLinkRoutes.Off)
                .CreateSetupOwnerAsync(_tenant, new NewSubject("Owner", Username: "owner"), CancellationToken.None);

            (await Subjects().GetAsync(created, CancellationToken.None)).Should().BeNull();
            RecordOf(_tenant).Should().BeNull("the subject and its record are saved together");

            await unit.CommitAsync(token => unit.Context.SaveChangesAsync(token), CancellationToken.None);
        }

        (await GetAsync(created)).Username.Should().Be("owner");
        RecordOf(_tenant).Should().Be(created);
        (await CandidateAsync()).Should().Be(created, "a subject recorded before its membership was written is still resumed");
    }

    private PasskeyCredentialRepository Passkeys() => new(new TestNocturneDbContextFactory(_db.CreateContext()));

    private static NewPasskeyCredential PasskeyOf(Guid subject) =>
        new(subject, Guid.NewGuid().ToByteArray(), [1], 0, [], null, null, Now);

    private static NewSubjectOidcIdentity IdentityOf(Guid subject, Guid provider, string sub) =>
        new(subject, provider, sub, "https://issuer.example", null, Now);

    /// <summary>The first credential stores, each storing for <paramref name="subject"/> on <see cref="_tenant"/>.</summary>
    private Task<Guid?> StoreFirstCredentialAsync(string store, Guid subject, string sub = "sub-1") => store switch
    {
        "passkey" => Passkeys().CreateForSetupOwnerAsync(_tenant, PasskeyOf(subject), CancellationToken.None),
        "provider identity" => Subjects().AddSetupOwnerOidcIdentityAsync(
            _tenant, IdentityOf(subject, _provider, sub), CancellationToken.None),
        _ => throw new ArgumentOutOfRangeException(nameof(store), store, null),
    };

    private int CredentialsOf(Guid subject)
    {
        using var db = _db.CreateContext();
        return db.PasskeyCredentials.Count(c => c.SubjectId == subject)
            + db.SubjectOidcIdentities.Count(o => o.SubjectId == subject);
    }

    [Theory]
    [InlineData("passkey")]
    [InlineData("provider identity")]
    public async Task TheFirstCredentialStoreClaimsTheSetupOfTheRecordedSubjectAndEndsOnlyItsTenantsRecord(string store)
    {
        var owner = await CreateSetupOwnerAsync(new NewSubject("Owner"));
        var elsewhere = await CreateSetupOwnerAsync(new NewSubject("Elsewhere"), _otherTenant);

        var stored = await StoreFirstCredentialAsync(store, owner);

        stored.Should().NotBeNull();
        CredentialsOf(owner).Should().Be(1);
        RecordOf(_tenant).Should().BeNull();
        RecordOf(_otherTenant).Should().Be(elsewhere);
        (await Subjects().GetAsync(owner, CancellationToken.None)).Should().NotBeNull("ending the record leaves the subject");
    }

    [Theory]
    [InlineData("passkey")]
    [InlineData("provider identity")]
    public async Task ACredentialStoreForASubjectTheRecordDoesNotNameStoresNothing(string store)
    {
        var owner = await CreateSetupOwnerAsync(new NewSubject("Owner"));
        var stranger = await CreateAsync(new NewSubject("Stranger"));

        var stored = await StoreFirstCredentialAsync(store, stranger);

        stored.Should().BeNull();
        CredentialsOf(stranger).Should().Be(0);
        RecordOf(_tenant).Should().Be(owner);
    }

    [Theory]
    [InlineData("passkey", "passkey")]
    [InlineData("passkey", "provider identity")]
    [InlineData("provider identity", "passkey")]
    [InlineData("provider identity", "provider identity")]
    public async Task OnceTheSetupIsClaimedASecondCredentialStoreStoresNothing(string first, string second)
    {
        var owner = await CreateSetupOwnerAsync(new NewSubject("Owner"));

        (await StoreFirstCredentialAsync(first, owner, "sub-1")).Should().NotBeNull();
        var again = await StoreFirstCredentialAsync(second, owner, "sub-2");

        again.Should().BeNull();
        CredentialsOf(owner).Should().Be(1);
    }

    [Fact]
    public async Task ACredentialStoreForATenantThatDoesNotExistStoresNothing()
    {
        var owner = await CreateSetupOwnerAsync(new NewSubject("Owner"));

        (await Passkeys().CreateForSetupOwnerAsync(Guid.CreateVersion7(), PasskeyOf(owner), CancellationToken.None))
            .Should().BeNull();
        CredentialsOf(owner).Should().Be(0);
    }

    [Fact]
    public async Task TheFirstCredentialStoreRunsOnlyOnARepositoryThatOpensItsOwnTransaction()
    {
        await using var unit = DbUnitOfWork.Own(_db.CreateContext());

        var passkey = () => new PasskeyCredentialRepository(unit)
            .CreateForSetupOwnerAsync(_tenant, PasskeyOf(Guid.CreateVersion7()), CancellationToken.None);
        var identity = () => new SubjectRepository(unit, AccountLinkRoutes.Off)
            .AddSetupOwnerOidcIdentityAsync(_tenant, IdentityOf(Guid.CreateVersion7(), _provider, "sub-1"), CancellationToken.None);

        await passkey.Should().ThrowAsync<InvalidOperationException>();
        await identity.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task TheSetupIsClaimedOnlyInsideATransaction()
    {
        var owner = await CreateSetupOwnerAsync(new NewSubject("Owner"));
        await using var db = _db.CreateContext();

        var claim = () => db.ClaimSetupAsync(_tenant, owner, CancellationToken.None);

        await claim.Should().ThrowAsync<InvalidOperationException>();
        RecordOf(_tenant).Should().Be(owner);
    }

    [Fact]
    public async Task DeletingTheRecordedSubjectEndsTheRecord()
    {
        var owner = await CreateSetupOwnerAsync(new NewSubject("Owner"));

        using (var db = _db.CreateContext())
        {
            db.Subjects.Remove(db.Subjects.Single(s => s.Id == owner));
            db.SaveChanges();
        }

        RecordOf(_tenant).Should().BeNull();
    }

    public static TheoryData<string> RefusedSetupOwnerHoldings() => new(
        "demo", "system", "inactive", "passkey", "provider link", "disabled provider link", "authenticator",
        "live session", "live Recovery code", "live OperatorRecovery code", "live EmailRecovery code",
        "live RecoverySession code", "live Login code", "confirmed address a link can reach");

    [Theory]
    [MemberData(nameof(RefusedSetupOwnerHoldings))]
    public async Task TheRecordedSetupOwnerIsNotResumedWhenItHolds(string held)
    {
        var owner = await CreateSetupOwnerAsync(new NewSubject("Owner"));
        var linkRoute = false;
        switch (held)
        {
            case "demo":
                EditSubject(owner, s => s.IsDemoSubject = true);
                break;
            case "system":
                EditSubject(owner, s => s.IsSystemSubject = true);
                break;
            case "inactive":
                EditSubject(owner, s => s.IsActive = false);
                break;
            case "passkey":
                AddPasskey(owner);
                break;
            case "provider link":
                await LinkAsync(owner, _provider, "ext");
                break;
            case "disabled provider link":
                await LinkAsync(owner, _disabledProvider, "ext", "https://old.example");
                break;
            case "authenticator":
                using (var db = _db.CreateContext())
                {
                    db.TotpCredentials.Add(new TotpCredentialEntity { Id = Guid.CreateVersion7(), SubjectId = owner, SecretKey = [0] });
                    db.SaveChanges();
                }
                break;
            case "live session":
                AddRefreshToken(owner, DateTime.UtcNow.AddDays(7));
                break;
            case "confirmed address a link can reach":
                EditSubject(owner, s =>
                {
                    s.SetEmail("owner@example.test");
                    s.ConfirmEmail("owner@example.test", DateTime.UtcNow);
                });
                linkRoute = true;
                break;
            default:
                AddLoginCode(owner, Enum.Parse<LoginCodePurpose>(held.Split(' ')[1]), DateTime.UtcNow.AddHours(1));
                break;
        }

        (await CandidateAsync(linkRoute: linkRoute)).Should().BeNull();
    }

    public static TheoryData<string> AdmittedSetupOwnerHoldings() => new(
        "expired session", "spent recovery code", "expired operator code", "live address confirmation",
        "unconfirmed address a link can reach", "confirmed address no link can reach", "platform admin",
        "membership of another tenant", "another global role", "no membership");

    [Theory]
    [MemberData(nameof(AdmittedSetupOwnerHoldings))]
    public async Task TheRecordedSetupOwnerIsResumedWhenItHolds(string held)
    {
        var owner = held == "no membership"
            ? await Subjects().CreateSetupOwnerAsync(_tenant, new NewSubject("Owner"), CancellationToken.None)
            : await CreateSetupOwnerAsync(new NewSubject("Owner"));
        var linkRoute = false;
        switch (held)
        {
            case "expired session":
                AddRefreshToken(owner, DateTime.UtcNow.AddDays(-1));
                break;
            case "spent recovery code":
                AddLoginCode(owner, LoginCodePurpose.Recovery, DateTime.UtcNow.AddHours(1), consumedAt: DateTime.UtcNow.AddMinutes(-1));
                break;
            case "expired operator code":
                AddLoginCode(owner, LoginCodePurpose.OperatorRecovery, DateTime.UtcNow.AddMinutes(-1));
                break;
            case "live address confirmation":
                AddLoginCode(owner, LoginCodePurpose.EmailConfirm, DateTime.UtcNow.AddHours(1));
                break;
            case "unconfirmed address a link can reach":
                EditSubject(owner, s => s.SetEmail("owner@example.test"));
                linkRoute = true;
                break;
            case "confirmed address no link can reach":
                EditSubject(owner, s =>
                {
                    s.SetEmail("owner@example.test");
                    s.ConfirmEmail("owner@example.test", DateTime.UtcNow);
                });
                break;
            case "platform admin":
                EditSubject(owner, s => s.IsPlatformAdmin = true);
                break;
            case "membership of another tenant":
                AddMember(_otherTenant, owner);
                break;
            case "another global role":
                using (var db = _db.CreateContext())
                {
                    db.SubjectRoles.Add(new SubjectRoleEntity
                    {
                        SubjectId = owner, RoleId = db.Roles.Single(r => r.Name == "public").Id, AssignedAt = Now,
                    });
                    db.SaveChanges();
                }
                break;
        }

        (await CandidateAsync(linkRoute: linkRoute)).Should().Be(owner);
    }

    [Fact]
    public async Task AccessRequestsAreListedNewestFirstAndOnlyOnceTheyHoldAPasskey()
    {
        var older = await CreateAsync(new NewSubject("Ann", IsActive: false, ApprovalStatus: "Pending", AccessRequestMessage: "hi"));
        var newer = await CreateAsync(new NewSubject("Ann", IsActive: false, ApprovalStatus: "Pending"));
        var bot = await CreateAsync(new NewSubject("Bot", IsSystemSubject: true, IsActive: false, ApprovalStatus: "Pending"));
        await CreateAsync(new NewSubject("Ann", IsActive: false, ApprovalStatus: "Pending"));
        AddPasskey(older);
        AddPasskey(newer);
        AddPasskey(bot);

        // The pending list orders by CreatedAt, which the context stamps at save; spread them out.
        SetCreatedAt(older, Now.AddMinutes(-2));
        SetCreatedAt(newer, Now.AddMinutes(-1));

        var pending = await Subjects().ListPendingAccessRequestsAsync(CancellationToken.None);
        pending.Select(s => s.Id).Should().Equal(newer, older);
        pending.Should().OnlyContain(s => s.Roles.Count == 0);
    }

    [Fact]
    public async Task AnEnrollingSubjectIsOneOfTheKindUnderTheNameItsOptionsStepCreatedHoldingNoWayToSignIn()
    {
        var requester = await CreateAsync(new NewSubject("Ann", IsActive: false, ApprovalStatus: "Pending"));
        var requesterWithPasskey = await CreateAsync(new NewSubject("Ann", IsActive: false, ApprovalStatus: "Pending"));
        AddPasskey(requesterWithPasskey);
        var system = await CreateAsync(new NewSubject("Ann", IsSystemSubject: true, IsActive: false, ApprovalStatus: "Pending"));
        var invitee = await CreateAsync(new NewSubject("Inv", Username: "inv"));
        var linked = await CreateAsync(new NewSubject("Inv2", Username: "inv"));
        await LinkAsync(linked, _provider, "ext");
        var linkedToDisabled = await CreateAsync(new NewSubject("Inv3", Username: "inv"));
        await LinkAsync(linkedToDisabled, _disabledProvider, "ext2", "https://old.example");
        var deactivated = await CreateAsync(new NewSubject("Inv4", Username: "inv", IsActive: false));
        var withAuthenticator = await CreateAsync(new NewSubject("Inv5", Username: "inv"));
        using (var db = _db.CreateContext())
        {
            db.TotpCredentials.Add(new TotpCredentialEntity { Id = Guid.CreateVersion7(), SubjectId = withAuthenticator, SecretKey = [0] });
            db.SaveChanges();
        }

        var withLiveSession = await CreateAsync(new NewSubject("Inv6", Username: "inv"));
        AddRefreshToken(withLiveSession, DateTime.UtcNow.AddDays(1));
        var withEndedSession = await CreateAsync(new NewSubject("Inv7", Username: "inv"));
        AddRefreshToken(withEndedSession, DateTime.UtcNow.AddDays(-1));

        async Task<bool> Enrolling(Guid id, EnrolmentMatch match) =>
            await Subjects().IsEnrollingSubjectAsync(id, match, CancellationToken.None);

        (await Enrolling(requester, new EnrolmentMatch.AccessRequest("Ann"))).Should().BeTrue();
        (await Enrolling(requester, new EnrolmentMatch.AccessRequest("Bea"))).Should().BeFalse("another name");
        (await Enrolling(requester, new EnrolmentMatch.Invite("ann"))).Should().BeFalse("an access request is not an invite's subject");
        (await Enrolling(requesterWithPasskey, new EnrolmentMatch.AccessRequest("Ann"))).Should().BeFalse("it holds a passkey");
        (await Enrolling(system, new EnrolmentMatch.AccessRequest("Ann"))).Should().BeFalse("a system subject");
        (await Enrolling(invitee, new EnrolmentMatch.Invite("inv"))).Should().BeTrue();
        (await Enrolling(invitee, new EnrolmentMatch.Invite("other"))).Should().BeFalse("another username");
        (await Enrolling(invitee, new EnrolmentMatch.AccessRequest("Inv"))).Should().BeFalse("an invite's subject is not an access request");
        (await Enrolling(linked, new EnrolmentMatch.Invite("inv"))).Should().BeFalse("it holds a provider link");
        (await Enrolling(linkedToDisabled, new EnrolmentMatch.Invite("inv"))).Should().BeFalse("a disabled provider can be enabled again");
        (await Enrolling(deactivated, new EnrolmentMatch.Invite("inv"))).Should().BeFalse("an invite's subject is active");
        (await Enrolling(withAuthenticator, new EnrolmentMatch.Invite("inv"))).Should().BeFalse("it holds an authenticator");
        (await Enrolling(withLiveSession, new EnrolmentMatch.Invite("inv"))).Should().BeFalse("it holds a live session");
        (await Enrolling(withEndedSession, new EnrolmentMatch.Invite("inv"))).Should().BeTrue("an ended session signs no one in");
        (await Enrolling(Guid.CreateVersion7(), new EnrolmentMatch.Invite("inv"))).Should().BeFalse("no such subject");
    }

    /// <summary>
    /// An enrolment that was abandoned leaves a subject with the username and no membership, so a
    /// member lookup by that username still finds only the member.
    /// </summary>
    [Fact]
    public async Task AnAbandonedEnrolmentUnderAMembersUsernameIsNotFoundAsAMember()
    {
        await CreateAsync(new NewSubject("Abandoned", Username: "sam", Id: Ordered(1)));
        var member = await CreateAsync(new NewSubject("Sam", Username: "sam", Id: Ordered(2)));
        AddMember(_tenant, member);
        await CreateAsync(new NewSubject("Abandoned again", Username: "sam", Id: Ordered(3)));

        (await Subjects().FindMemberByUsernameAsync(_tenant, "sam", activeOnly: true, Now, CancellationToken.None))!
            .Id.Should().Be(member);
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

        (await Subjects().UpdateAsync(Guid.CreateVersion7(), new SubjectEdit(Name: "x"), CancellationToken.None)).Should().BeOfType<SubjectChange.NotFound>();
        (await Subjects().UpdateAsync(Guid.CreateVersion7(), new SubjectEdit(IsActive: false), CancellationToken.None)).Should().BeOfType<SubjectChange.NotFound>();
        (await Subjects().UpdateProfileAsync(Guid.CreateVersion7(), new SubjectProfile("x", null, null), CancellationToken.None)).Should().BeNull();
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
        (await Subjects().CountStrandedByProviderAsync(_provider, CancellationToken.None))
            .Should().Be(1, "the subject with a passkey too is not stranded");
        (await Subjects().CountStrandedByProviderAsync(_disabledProvider, CancellationToken.None)).Should().Be(0);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public async Task AMemberWhoseOnlyIdentityIsAtTheProviderIsStrandedUnlessALinkCanReachTheirConfirmedAddress(
        bool delivery, int stranded)
    {
        var member = await CreateAsync(new NewSubject("Linked"));
        AddMember(_tenant, member);
        await LinkAsync(member, _provider, "ext-1");
        using (var db = _db.CreateContext())
        {
            var row = db.Subjects.Single(s => s.Id == member);
            row.SetEmail("linked@example.test");
            row.ConfirmEmail("linked@example.test", DateTime.UtcNow);
            db.SaveChanges();
        }
        var subjects = new SubjectRepository(
            new TestNocturneDbContextFactory(_db.CreateContext(_tenant)), AccountLinkRoutes.Of(delivery));

        (await subjects.CountStrandedByProviderAsync(_provider, CancellationToken.None)).Should().Be(stranded);
    }

    [Fact]
    public async Task WritesOnAUnitStayStagedUntilItsOwnerCommits()
    {
        Guid created;
        await using (var unit = DbUnitOfWork.Own(_db.CreateContext()))
        {
            var staged = new SubjectRepository(unit, AccountLinkRoutes.Off);
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
        var cannotPin = () => new SubjectRepository(pinned, AccountLinkRoutes.Off).IsDemoSubjectAsync(created, CancellationToken.None);
        await cannotPin.Should().ThrowAsync<InvalidOperationException>("a unit runs on its own context and pins no other");
        var cannotDelete = () => new SubjectRepository(pinned, AccountLinkRoutes.Off).DeleteAsync(created, CancellationToken.None);
        await cannotDelete.Should().ThrowAsync<InvalidOperationException>("a unit's owner commits; the delete's transaction is its own");
        var cannotRemove = () => new SubjectRepository(pinned, AccountLinkRoutes.Off).TryRemoveFactorAsync(created, new SignInFactor.Passkey(Guid.NewGuid()), CancellationToken.None);
        await cannotRemove.Should().ThrowAsync<InvalidOperationException>();
    }
}

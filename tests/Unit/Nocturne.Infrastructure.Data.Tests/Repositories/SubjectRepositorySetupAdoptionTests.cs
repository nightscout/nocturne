using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// The two predicates behind <see cref="ISubjectRepository.AdoptForSetupAsync"/> and
/// <see cref="ISubjectRepository.TakeUnclaimedOidcIdentityAsync"/>, one clause at a time, over SQLite.
/// Row level security and serializable isolation are not under test: SQLite has neither, so these pin
/// which rows each clause names, and the API integration tests pin the same rules through the pipeline.
/// </summary>
public class SubjectRepositorySetupAdoptionTests : IDisposable
{
    private const string Slug = "test";

    private readonly Guid _tenant = Guid.CreateVersion7();
    private readonly Guid _otherTenant = Guid.CreateVersion7();
    private readonly Guid _provider = Guid.CreateVersion7();
    private readonly Guid _adminRole = Guid.CreateVersion7();
    private readonly Guid _publicRole = Guid.CreateVersion7();
    private readonly Dictionary<Guid, Guid> _ownerRoles = [];
    private readonly SqliteTestDatabase _db;

    public SubjectRepositorySetupAdoptionTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(_tenant, Slug).SeedTenant(_otherTenant, "other");
        using var db = _db.CreateContext();
        db.Roles.Add(new RoleEntity { Id = _adminRole, Name = GlobalRoles.Admin, Permissions = ["*"], IsSystemRole = true });
        db.Roles.Add(new RoleEntity { Id = _publicRole, Name = "public", Permissions = ["glucose:read"] });
        db.OidcProviders.Add(new OidcProviderEntity
        {
            Id = _provider, Name = "Keycloak", IssuerUrl = "https://issuer.example", ClientId = "nocturne", IsEnabled = true,
        });
        foreach (var tenant in new[] { _tenant, _otherTenant })
        {
            var id = Guid.CreateVersion7();
            _ownerRoles[tenant] = id;
            db.TenantRoles.Add(new TenantRoleEntity
            {
                Id = id, TenantId = tenant, Name = "Owner", Slug = RoleSeeds.Owner, IsSystem = true,
            });
        }
        db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    public static TheoryData<string> TablesNamingASubject() => new(
        "recovery code", "refresh token", "trusted device", "step-up token", "avatar", "chat identity",
        "tenant creation invite it created", "role it assigned", "team membership", "team it created",
        "team invite it created", "team access link it created");

    public static TheoryData<string> TenantRowsNamingASubject() => new(
        "login code", "member invite it created", "grant it holds", "grant it created", "authorization code",
        "excursion mute", "membership request", "membership request it decided", "client device", "device code",
        "alert invite it created", "alert invite it used", "excursion receipt", "team access it approved");

    public static TheoryData<string> AdoptedSubjectClauses() => new(
        "inactive", "system", "demo", "platform admin", "member of the setup tenant", "member of another tenant",
        "passkey", "authenticator", "second provider identity");

    public static TheoryData<string> OwnerStepSubjectClauses() => new(
        "inactive", "system", "demo", "platform admin", "last login", "passkey", "authenticator",
        "provider identity", "only membership is in another tenant", "two memberships", "no owner role",
        "global role other than admin");

    public static TheoryData<string> TakenFromClauses() => new(
        "inactive", "system", "demo", "platform admin", "last login", "second provider identity",
        "member of a tenant", "passkey", "authenticator");

    private SubjectRepository Subjects() =>
        new(new TestNocturneDbContextFactory(_db.CreateContext()), AccountLinkRoutes.Off);

    private Guid NewSubject(string name, Action<SubjectEntity>? edit = null)
    {
        using var db = _db.CreateContext();
        var subject = new SubjectEntity { Id = Guid.CreateVersion7(), Name = name, Username = name.ToLowerInvariant(), IsActive = true };
        edit?.Invoke(subject);
        db.Subjects.Add(subject);
        db.SaveChanges();
        return subject.Id;
    }

    private Guid Link(Guid subject, string sub)
    {
        using var db = _db.CreateContext();
        var identity = new SubjectOidcIdentityEntity
        {
            Id = Guid.CreateVersion7(), SubjectId = subject, ProviderId = _provider, OidcSubjectId = sub,
            Issuer = "https://issuer.example", LinkedAt = DateTime.UtcNow,
        };
        db.SubjectOidcIdentities.Add(identity);
        db.SaveChanges();
        return identity.Id;
    }

    private void Member(Guid tenant, Guid subject, bool owner)
    {
        using var db = _db.CreateContext();
        var member = new TenantMemberEntity { Id = Guid.CreateVersion7(), TenantId = tenant, SubjectId = subject };
        db.TenantMembers.Add(member);
        if (owner)
            db.TenantMemberRoles.Add(new TenantMemberRoleEntity
            {
                Id = Guid.CreateVersion7(), TenantMemberId = member.Id, TenantRoleId = _ownerRoles[tenant],
            });
        db.SaveChanges();
    }

    private void GlobalRole(Guid subject, Guid role, Guid? assignedBy = null)
    {
        using var db = _db.CreateContext();
        db.SubjectRoles.Add(new SubjectRoleEntity { SubjectId = subject, RoleId = role, AssignedById = assignedBy });
        db.SaveChanges();
    }

    private void Passkey(Guid subject)
    {
        using var db = _db.CreateContext();
        db.PasskeyCredentials.Add(new PasskeyCredentialEntity
        {
            Id = Guid.CreateVersion7(), SubjectId = subject, CredentialId = Guid.NewGuid().ToByteArray(), PublicKey = [1],
        });
        db.SaveChanges();
    }

    private void Authenticator(Guid subject)
    {
        using var db = _db.CreateContext();
        db.TotpCredentials.Add(new TotpCredentialEntity { Id = Guid.CreateVersion7(), SubjectId = subject });
        db.SaveChanges();
    }

    private void NameSubjectInATable(string table, Guid subject)
    {
        using var db = _db.CreateContext();
        var third = NewSubject($"third-{Guid.NewGuid():N}");
        var expires = DateTime.UtcNow.AddDays(1);
        switch (table)
        {
            case "recovery code":
                db.RecoveryCodes.Add(new RecoveryCodeEntity { Id = Guid.CreateVersion7(), SubjectId = subject, CodeHash = "h" });
                break;
            case "refresh token":
                db.RefreshTokens.Add(RefreshToken(subject));
                break;
            case "trusted device":
                db.TrustedDevices.Add(new TrustedDeviceEntity { Id = Guid.CreateVersion7(), SubjectId = subject, TokenHash = "h", ExpiresAt = expires });
                break;
            case "step-up token":
                db.TotpStepUpTokens.Add(new TotpStepUpTokenEntity { Id = Guid.CreateVersion7(), SubjectId = subject, ExpiresAt = expires });
                break;
            case "avatar":
                db.SubjectAvatars.Add(new SubjectAvatarEntity { Id = Guid.CreateVersion7(), SubjectId = subject, Data = [1], FileSize = 1 });
                break;
            case "chat identity":
                db.ChatIdentityDirectory.Add(new ChatIdentityDirectoryEntry
                {
                    Id = Guid.CreateVersion7(), Platform = "discord", PlatformUserId = "u", TenantId = _tenant,
                    NocturneUserId = subject, Label = "l", DisplayName = "d",
                });
                break;
            case "tenant creation invite it created":
                db.TenantCreationInvites.Add(new TenantCreationInviteEntity
                {
                    Id = Guid.CreateVersion7(), CreatedBySubjectId = subject, TokenHash = "h", ExpiresAt = expires,
                });
                break;
            case "role it assigned":
                db.SubjectRoles.Add(new SubjectRoleEntity { SubjectId = third, RoleId = _publicRole, AssignedById = subject });
                break;
            case "team membership":
                db.TeamMembers.Add(new TeamMemberEntity { Id = Guid.CreateVersion7(), TeamId = Team(db, third), SubjectId = subject });
                break;
            case "team it created":
                Team(db, subject);
                break;
            case "team invite it created":
                db.TeamInvites.Add(new TeamInviteEntity
                {
                    Id = Guid.CreateVersion7(), TeamId = Team(db, third), CreatedBySubjectId = subject, TokenHash = "h", ExpiresAt = expires,
                });
                break;
            case "team access link it created":
                db.TeamAccessLinks.Add(new TeamAccessLinkEntity
                {
                    Id = Guid.CreateVersion7(), TeamId = Team(db, third), CreatedBySubjectId = subject, TokenHash = "h",
                    ExpiresAt = expires, SuggestedRoleSlug = RoleSeeds.Owner,
                });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(table), table, null);
        }

        db.SaveChanges();
    }

    private static Guid Team(NocturneDbContext db, Guid createdBy)
    {
        var team = new TeamEntity { Id = Guid.CreateVersion7(), Name = "Team", CreatedBySubjectId = createdBy };
        db.Teams.Add(team);
        return team.Id;
    }

    private static RefreshTokenEntity RefreshToken(Guid subject) => new()
    {
        Id = Guid.CreateVersion7(), SubjectId = subject, TokenHash = Guid.NewGuid().ToString("N"),
        IssuedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddDays(1),
    };

    /// <summary>
    /// These tables carry the subject id with no foreign key to the subjects table, and some other columns
    /// carry one the row does not need, so foreign keys are off while the row is saved. The row sits in the
    /// other tenant: the check reads every tenant's rows.
    /// </summary>
    private void NameSubjectInATenantRow(string row, Guid subject)
    {
        using var db = _db.CreateContext(_otherTenant);
        var tenant = _otherTenant;
        var other = Guid.CreateVersion7();
        var expires = DateTime.UtcNow.AddDays(1);
        switch (row)
        {
            case "login code":
                db.LoginCodes.Add(new LoginCodeEntity { Id = Guid.CreateVersion7(), TenantId = tenant, SubjectId = subject, CodeHash = "h", ExpiresAt = expires });
                break;
            case "member invite it created":
                db.MemberInvites.Add(new MemberInviteEntity { Id = Guid.CreateVersion7(), TenantId = tenant, CreatedBySubjectId = subject, TokenHash = "h", ExpiresAt = expires });
                break;
            case "grant it holds":
                db.OAuthGrants.Add(new OAuthGrantEntity { Id = Guid.CreateVersion7(), TenantId = tenant, SubjectId = subject });
                break;
            case "grant it created":
                db.OAuthGrants.Add(new OAuthGrantEntity { Id = Guid.CreateVersion7(), TenantId = tenant, SubjectId = other, CreatedBySubjectId = subject });
                break;
            case "authorization code":
                db.OAuthAuthorizationCodes.Add(new OAuthAuthorizationCodeEntity
                {
                    Id = Guid.CreateVersion7(), TenantId = tenant, ClientEntityId = other, SubjectId = subject,
                    CodeHash = "h", RedirectUri = "https://app.example/cb", CodeChallenge = "c", ExpiresAt = expires,
                });
                break;
            case "excursion mute":
                db.AlertExcursionMutes.Add(new AlertExcursionMuteEntity { Id = Guid.CreateVersion7(), TenantId = tenant, SubjectId = subject, AlertExcursionId = other });
                break;
            case "membership request":
                db.MembershipRequests.Add(new MembershipRequestEntity { Id = Guid.CreateVersion7(), TenantId = tenant, SubjectId = subject });
                break;
            case "membership request it decided":
                db.MembershipRequests.Add(new MembershipRequestEntity { Id = Guid.CreateVersion7(), TenantId = tenant, SubjectId = other, DecidedBySubjectId = subject });
                break;
            case "client device":
                db.ClientDevices.Add(new ClientDeviceEntity { Id = Guid.CreateVersion7(), TenantId = tenant, SubjectId = subject, InstallId = "i", Kind = "k" });
                break;
            case "device code":
                db.OAuthDeviceCodes.Add(new OAuthDeviceCodeEntity
                {
                    Id = Guid.CreateVersion7(), TenantId = tenant, ClientId = "c", DeviceCodeHash = "h", UserCode = "u",
                    ExpiresAt = expires, SubjectId = subject,
                });
                break;
            case "alert invite it created":
                db.AlertInvites.Add(new AlertInviteEntity { Id = Guid.CreateVersion7(), TenantId = tenant, CreatedBy = subject, Token = "t1", AlertRuleChannelId = other, ExpiresAt = expires });
                break;
            case "alert invite it used":
                db.AlertInvites.Add(new AlertInviteEntity { Id = Guid.CreateVersion7(), TenantId = tenant, CreatedBy = other, UsedBy = subject, Token = "t2", AlertRuleChannelId = other, ExpiresAt = expires });
                break;
            case "excursion receipt":
                db.AlertExcursionReceipts.Add(new AlertExcursionReceiptEntity
                {
                    Id = Guid.CreateVersion7(), TenantId = tenant, AlertExcursionId = other,
                    ActorKind = AlertReceiptActorKind.Member, ActorId = subject, SeenAt = DateTime.UtcNow,
                });
                break;
            case "team access it approved":
                db.TeamTenantAccess.Add(new TeamTenantAccessEntity
                {
                    Id = Guid.CreateVersion7(), TeamId = other, TenantId = tenant, ApprovedBySubjectId = subject,
                });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(row), row, null);
        }

        db.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF");
        try
        {
            db.SaveChanges();
        }
        finally
        {
            db.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON");
        }
    }

    /// <summary>What a refusal must leave as it found it: every row the two predicates read or the writes touch.</summary>
    private async Task<string> SnapshotAsync()
    {
        await using var db = _db.CreateContext();
        var lines = new List<string>();
        lines.AddRange((await db.Subjects.IgnoreQueryFilters().AsNoTracking().ToListAsync())
            .Select(s => $"subject {s.Id} {s.Name} {s.Username} {s.IsActive}"));
        lines.AddRange((await db.TenantMembers.IgnoreQueryFilters().AsNoTracking().ToListAsync())
            .Select(m => $"member {m.Id} {m.SubjectId} {m.TenantId}"));
        lines.AddRange((await db.SubjectRoles.AsNoTracking().ToListAsync())
            .Select(r => $"role {r.SubjectId} {r.RoleId} {r.AssignedById}"));
        lines.AddRange((await db.SubjectOidcIdentities.AsNoTracking().ToListAsync())
            .Select(i => $"identity {i.Id} {i.SubjectId} {i.Email}"));
        lines.AddRange((await db.RefreshTokens.AsNoTracking().ToListAsync())
            .Select(t => $"token {t.Id} {t.RevokedAt}"));
        lines.AddRange((await db.Tenants.AsNoTracking().ToListAsync())
            .Select(t => $"tenant {t.Id} {t.SetupOwnerSubjectId}"));
        lines.Sort(StringComparer.Ordinal);
        return string.Join('\n', lines);
    }

    private async Task<bool> SubjectExistsAsync(Guid id)
    {
        await using var db = _db.CreateContext();
        return await db.Subjects.IgnoreQueryFilters().AnyAsync(s => s.Id == id);
    }

    private (Guid Created, Guid Holder, Guid Identity) Adoption(
        Action<Guid, Guid>? shapeHolder = null, Action<Guid>? shapeCreated = null, bool createdIsOwnerOfSetup = true)
    {
        var created = NewSubject("Owner Name", s => s.Username = "owner");
        if (createdIsOwnerOfSetup)
            Member(_tenant, created, owner: true);
        RecordSetupOwner(created);
        GlobalRole(created, _adminRole);
        var holder = NewSubject("Holder", s => s.Username = null);
        var identity = Link(holder, "ext-holder");
        shapeHolder?.Invoke(holder, identity);
        shapeCreated?.Invoke(created);
        return (created, holder, identity);
    }

    private void RecordSetupOwner(Guid? subject)
    {
        using var db = _db.CreateContext();
        db.Tenants.Single(t => t.Id == _tenant).SetupOwnerSubjectId = subject;
        db.SaveChanges();
    }

    private async Task AssertAdoptionRefusedAsync(Guid created, Guid holder, Guid identity, string slug = Slug)
    {
        var before = await SnapshotAsync();

        (await Subjects().AdoptForSetupAsync(created, holder, identity, slug, CancellationToken.None))
            .Should().BeFalse();

        (await SnapshotAsync()).Should().Be(before, "a refused adoption changes nothing");
    }

    [Fact]
    public async Task TheSubjectHoldingTheIdentityBecomesTheOwnerAndTheOwnerStepSubjectGoes()
    {
        var (created, holder, identity) = Adoption(shapeHolder: (h, _) => NameSubjectInATable("refresh token", h));

        (await Subjects().AdoptForSetupAsync(created, holder, identity, Slug, CancellationToken.None)).Should().BeTrue();

        await using var db = _db.CreateContext();
        (await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == _tenant)).SetupOwnerSubjectId
            .Should().BeNull("the adoption is the first credential store, which claims the setup");
        var adopted = await db.Subjects.AsNoTracking().SingleAsync(s => s.Id == holder);
        adopted.Should().BeEquivalentTo(new { Name = "Owner Name", Username = "owner", IsActive = true });
        (await db.TenantMembers.AsNoTracking().Where(m => m.TenantId == _tenant).Select(m => m.SubjectId).ToListAsync())
            .Should().Equal([holder], "the owner membership moves to the subject holding the identity");
        (await db.TenantMemberRoles.CountAsync(r => r.TenantRole.Slug == RoleSeeds.Owner)).Should().Be(1);
        (await db.SubjectRoles.Where(r => r.SubjectId == holder).Select(r => r.RoleId).ToListAsync())
            .Should().Equal(_adminRole);
        (await db.SubjectOidcIdentities.AsNoTracking().SingleAsync(i => i.Id == identity)).SubjectId.Should().Be(holder);
        (await db.RefreshTokens.AsNoTracking().Where(t => t.SubjectId == holder).ToListAsync())
            .Should().ContainSingle().Which.Should().BeEquivalentTo(new { RevokedReason = "Adopted as the setup owner" })
            .And.Subject.As<RefreshTokenEntity>().RevokedAt.Should().NotBeNull();
        (await SubjectExistsAsync(created)).Should().BeFalse("the owner-step subject is removed");
    }

    [Fact]
    public async Task AdoptionPassesOnTheRolesTheHolderLacksAndNoDuplicate()
    {
        var (created, holder, identity) = Adoption(shapeHolder: (h, _) => GlobalRole(h, _adminRole));

        (await Subjects().AdoptForSetupAsync(created, holder, identity, Slug, CancellationToken.None)).Should().BeTrue();

        await using var db = _db.CreateContext();
        (await db.SubjectRoles.CountAsync(r => r.SubjectId == holder && r.RoleId == _adminRole)).Should().Be(1);
    }

    [Theory]
    [MemberData(nameof(AdoptedSubjectClauses))]
    public async Task AdoptionRefusesASubjectThat(string clause)
    {
        var (created, holder, identity) = Adoption(shapeHolder: (h, _) =>
        {
            switch (clause)
            {
                case "inactive": Edit(h, s => s.IsActive = false); break;
                case "system": Edit(h, s => s.IsSystemSubject = true); break;
                case "demo": Edit(h, s => s.IsDemoSubject = true); break;
                case "platform admin": Edit(h, s => s.IsPlatformAdmin = true); break;
                case "member of the setup tenant": Member(_tenant, h, owner: false); break;
                case "member of another tenant": Member(_otherTenant, h, owner: false); break;
                case "passkey": Passkey(h); break;
                case "authenticator": Authenticator(h); break;
                case "second provider identity": Link(h, "ext-second"); break;
                default: throw new ArgumentOutOfRangeException(nameof(clause), clause, null);
            }
        });

        await AssertAdoptionRefusedAsync(created, holder, identity);
    }

    [Fact]
    public async Task AdoptionRefusesAnIdentityThatAnotherSubjectHolds()
    {
        var (created, holder, _) = Adoption();
        var elsewhere = Link(NewSubject("Elsewhere"), "ext-elsewhere");

        await AssertAdoptionRefusedAsync(created, holder, elsewhere);
    }

    [Theory]
    [InlineData("another subject")]
    [InlineData("no subject")]
    public async Task AdoptionRefusesAnOwnerStepSubjectTheSetupOwnerRecordNamesNot(string recorded)
    {
        var (created, holder, identity) = Adoption();
        RecordSetupOwner(recorded == "another subject" ? NewSubject("Other") : null);

        await AssertAdoptionRefusedAsync(created, holder, identity);
    }

    [Fact]
    public async Task AdoptionRefusesAnUnknownTenant()
    {
        var (created, holder, identity) = Adoption();

        await AssertAdoptionRefusedAsync(created, holder, identity, "nowhere");
    }

    [Fact]
    public async Task AdoptionRefusesTheSameSubjectOnBothSides()
    {
        var (_, holder, identity) = Adoption();
        Member(_tenant, holder, owner: true);

        (await Subjects().AdoptForSetupAsync(holder, holder, identity, Slug, CancellationToken.None)).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(OwnerStepSubjectClauses))]
    public async Task AdoptionRefusesAnOwnerStepSubjectThat(string clause)
    {
        var (created, holder, identity) = Adoption(
            shapeCreated: c =>
            {
                switch (clause)
                {
                    case "inactive": Edit(c, s => s.IsActive = false); break;
                    case "system": Edit(c, s => s.IsSystemSubject = true); break;
                    case "demo": Edit(c, s => s.IsDemoSubject = true); break;
                    case "platform admin": Edit(c, s => s.IsPlatformAdmin = true); break;
                    case "last login": Edit(c, s => s.LastLoginAt = DateTime.UtcNow); break;
                    case "passkey": Passkey(c); break;
                    case "authenticator": Authenticator(c); break;
                    case "provider identity": Link(c, "ext-created"); break;
                    case "two memberships": Member(_otherTenant, c, owner: true); break;
                    case "global role other than admin": GlobalRole(c, _publicRole); break;
                    case "only membership is in another tenant":
                    case "no owner role":
                        break;
                    default: throw new ArgumentOutOfRangeException(nameof(clause), clause, null);
                }
            },
            createdIsOwnerOfSetup: clause is not ("only membership is in another tenant" or "no owner role"));

        switch (clause)
        {
            case "only membership is in another tenant": Member(_otherTenant, created, owner: true); break;
            case "no owner role": Member(_tenant, created, owner: false); break;
        }

        await AssertAdoptionRefusedAsync(created, holder, identity);
    }

    [Theory]
    [MemberData(nameof(TablesNamingASubject))]
    public async Task AdoptionRefusesAnOwnerStepSubjectNamedByTheTable(string table)
    {
        var (created, holder, identity) = Adoption(shapeCreated: c => NameSubjectInATable(table, c));

        await AssertAdoptionRefusedAsync(created, holder, identity);
    }

    [Theory]
    [MemberData(nameof(TenantRowsNamingASubject))]
    public async Task AdoptionRefusesAnOwnerStepSubjectNamedByARowOfAnyTenant(string row)
    {
        var (created, holder, identity) = Adoption(shapeCreated: c => NameSubjectInATenantRow(row, c));

        await AssertAdoptionRefusedAsync(created, holder, identity);
    }

    private (Guid Holder, Guid Taker, Guid Identity) TakeScene(Action<Guid>? shapeHolder = null)
    {
        var holder = NewSubject("Holder", s => s.Username = null);
        var identity = Link(holder, "ext-holder");
        var taker = NewSubject("Taker");
        shapeHolder?.Invoke(holder);
        return (holder, taker, identity);
    }

    private async Task AssertTakeRefusedAsync(Guid identity, Guid taker)
    {
        var before = await SnapshotAsync();

        (await Subjects().TakeUnclaimedOidcIdentityAsync(identity, taker, "t@x.example", CancellationToken.None))
            .Should().BeNull();

        (await SnapshotAsync()).Should().Be(before, "a refused take changes nothing");
    }

    [Fact]
    public async Task AnUnclaimedHolderIsDeletedAndItsIdentityMovesToTheTaker()
    {
        var (holder, taker, identity) = TakeScene(h => GlobalRole(h, _publicRole));

        var taken = await Subjects().TakeUnclaimedOidcIdentityAsync(identity, taker, "t@x.example", CancellationToken.None);

        taken.Should().NotBeNull();
        (await SubjectExistsAsync(holder)).Should().BeFalse("nothing but its own roles named the holder");
        await using var db = _db.CreateContext();
        var identities = await db.SubjectOidcIdentities.AsNoTracking().ToListAsync();
        identities.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Id = taken, SubjectId = taker, OidcSubjectId = "ext-holder", Email = "t@x.example", ProviderId = _provider,
        });
    }

    [Theory]
    [MemberData(nameof(TakenFromClauses))]
    public async Task TakingRefusesAHolderThat(string clause)
    {
        var (_, taker, identity) = TakeScene(h =>
        {
            switch (clause)
            {
                case "inactive": Edit(h, s => s.IsActive = false); break;
                case "system": Edit(h, s => s.IsSystemSubject = true); break;
                case "demo": Edit(h, s => s.IsDemoSubject = true); break;
                case "platform admin": Edit(h, s => s.IsPlatformAdmin = true); break;
                case "last login": Edit(h, s => s.LastLoginAt = DateTime.UtcNow); break;
                case "second provider identity": Link(h, "ext-second"); break;
                case "member of a tenant": Member(_otherTenant, h, owner: false); break;
                case "passkey": Passkey(h); break;
                case "authenticator": Authenticator(h); break;
                default: throw new ArgumentOutOfRangeException(nameof(clause), clause, null);
            }
        });

        await AssertTakeRefusedAsync(identity, taker);
    }

    [Theory]
    [MemberData(nameof(TablesNamingASubject))]
    public async Task TakingRefusesAHolderNamedByTheTable(string table)
    {
        var (_, taker, identity) = TakeScene(h => NameSubjectInATable(table, h));

        await AssertTakeRefusedAsync(identity, taker);
    }

    [Theory]
    [MemberData(nameof(TenantRowsNamingASubject))]
    public async Task TakingRefusesAHolderNamedByARowOfAnyTenant(string row)
    {
        var (_, taker, identity) = TakeScene(h => NameSubjectInATenantRow(row, h));

        await AssertTakeRefusedAsync(identity, taker);
    }

    [Fact]
    public async Task TakingRefusesAnIdentityTheTakerAlreadyHolds()
    {
        var taker = NewSubject("Taker");
        var identity = Link(taker, "ext-taker");

        await AssertTakeRefusedAsync(identity, taker);
    }

    [Fact]
    public async Task TakingRefusesAnUnknownIdentity()
    {
        var taker = NewSubject("Taker");

        await AssertTakeRefusedAsync(Guid.CreateVersion7(), taker);
    }

    private void Edit(Guid subject, Action<SubjectEntity> edit)
    {
        using var db = _db.CreateContext();
        edit(db.Subjects.Single(s => s.Id == subject));
        db.SaveChanges();
    }
}

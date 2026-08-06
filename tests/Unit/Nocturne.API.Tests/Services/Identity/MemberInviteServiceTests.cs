using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Services.Identity;

public class MemberInviteServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<NocturneDbContext> _dbOptions;
    private readonly NocturneDbContext _dbContext;
    private readonly Mock<IJwtService> _jwtService;
    private readonly Mock<ITenantService> _tenantService;
    private readonly MemberInviteService _service;

    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _creatorSubjectId = Guid.CreateVersion7();
    private readonly Guid _acceptorSubjectId = Guid.CreateVersion7();
    private Guid _followerRoleId;

    /// <summary>Granter permissions of a tenant owner: satisfies any grant the invite can carry.</summary>
    private static readonly string[] OwnerPermissions = [TenantPermissions.Superuser];

    private const string FakeToken = "fake-random-token-abc123";
    private const string FakeTokenHash = "hashed-fake-token";
    private const string BaseDomain = "app.nocturnecgm.com";

    public MemberInviteServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        _dbContext = new NocturneDbContext(_dbOptions);
        _dbContext.Database.EnsureCreated();
        // member_invites is tenant-scoped, so the context carries the tenant the request resolved
        // to. In the app TenantResolutionMiddleware pins it before any handler runs; here it stands
        // in for that pin, and without it every read below is filtered to nothing.
        _dbContext.TenantId = _tenantId;

        _jwtService = new Mock<IJwtService>();
        _jwtService.Setup(j => j.GenerateRefreshToken()).Returns(FakeToken);
        _jwtService.Setup(j => j.HashRefreshToken(FakeToken)).Returns(FakeTokenHash);

        _tenantService = new Mock<ITenantService>();

        var logger = new Mock<ILogger<MemberInviteService>>();

        _service = new MemberInviteService(
            _dbContext,
            _jwtService.Object,
            _tenantService.Object,
            new TenantRoleService(
                _dbContext,
                // Only SeedRolesForTenantAsync takes a context of its own, and nothing here seeds.
                Mock.Of<IDbContextFactory<NocturneDbContext>>()),
            Options.Create(new BaseDomainOptions { BaseDomain = BaseDomain }),
            logger.Object);

        // Seed tenant and subjects
        SeedData();
    }

    private void SeedData()
    {
        _dbContext.Tenants.Add(new TenantEntity
        {
            Id = _tenantId,
            Slug = "test",
            DisplayName = "Test Tenant",
        });

        _dbContext.Subjects.Add(new SubjectEntity
        {
            Id = _creatorSubjectId,
            Name = "Creator User",
        });

        _dbContext.Subjects.Add(new SubjectEntity
        {
            Id = _acceptorSubjectId,
            Name = "Acceptor User",
        });

        // Seed a follower role for the tenant
        _followerRoleId = Guid.CreateVersion7();
        _dbContext.TenantRoles.Add(new TenantRoleEntity
        {
            Id = _followerRoleId,
            TenantId = _tenantId,
            Name = "Follower",
            Slug = "follower",
            Permissions = [TenantPermissions.GlucoseRead, TenantPermissions.ReportsRead],
            IsSystem = true,
            SysCreatedAt = DateTime.UtcNow,
            SysUpdatedAt = DateTime.UtcNow,
        });

        _dbContext.SaveChanges();
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task CreateInviteAsync_ReturnsTokenAndUrl()
    {
        var result = await _service.CreateInviteAsync(
            _tenantId,
            _creatorSubjectId,
            OwnerPermissions,
            [_followerRoleId]);

        result.Token.Should().Be(FakeToken);
        result.InviteUrl.Should().Be($"https://{BaseDomain}/join?token={FakeToken}");
        result.Id.Should().NotBeEmpty();
        result.ExpiresAt.Should().BeAfter(DateTime.UtcNow);

        // Verify entity was persisted
        var entity = await _dbContext.MemberInvites.FirstOrDefaultAsync();
        entity.Should().NotBeNull();
        entity!.TokenHash.Should().Be(FakeTokenHash);
        entity.TenantId.Should().Be(_tenantId);
        entity.RoleIds.Should().Contain(_followerRoleId);
    }

    [Fact]
    public async Task CreateInviteAsync_RequiresAtLeastOneRoleOrPermission()
    {
        var act = () => _service.CreateInviteAsync(
            _tenantId,
            _creatorSubjectId,
            OwnerPermissions,
            []);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*At least one role or direct permission*");
    }

    [Fact]
    public async Task CreateInviteAsync_WithDirectPermissions_Succeeds()
    {
        var result = await _service.CreateInviteAsync(
            _tenantId,
            _creatorSubjectId,
            OwnerPermissions,
            [],
            directPermissions: [TenantPermissions.GlucoseRead]);

        result.Token.Should().Be(FakeToken);

        var entity = await _dbContext.MemberInvites.FirstOrDefaultAsync();
        entity.Should().NotBeNull();
        entity!.DirectPermissions.Should().Contain(TenantPermissions.GlucoseRead);
    }

    [Fact]
    public async Task CreateInviteAsync_RejectsSuperuserFromNonSuperuserCreator()
    {
        var act = () => _service.CreateInviteAsync(
            _tenantId,
            _creatorSubjectId,
            [TenantPermissions.MembersInvite, TenantPermissions.MembersManage],
            [],
            directPermissions: [TenantPermissions.Superuser]);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Cannot grant '*'*");

        (await _dbContext.MemberInvites.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task CreateInviteAsync_RejectsPermissionTheCreatorDoesNotHold()
    {
        var act = () => _service.CreateInviteAsync(
            _tenantId,
            _creatorSubjectId,
            [TenantPermissions.MembersInvite, TenantPermissions.GlucoseRead],
            [],
            directPermissions: [TenantPermissions.TreatmentsReadWrite]);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*treatments.readwrite*");
    }

    [Fact]
    public async Task CreateInviteAsync_RejectsUnknownPermission()
    {
        var act = () => _service.CreateInviteAsync(
            _tenantId,
            _creatorSubjectId,
            OwnerPermissions,
            [],
            directPermissions: ["glucose.destroy"]);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*not a known permission*");
    }

    [Fact]
    public async Task CreateInviteAsync_RejectsRoleCarryingPermissionsTheCreatorLacks()
    {
        // The follower role carries glucose.read; a creator holding only members.invite may not
        // hand it out even though the role ID is valid for the tenant.
        var act = () => _service.CreateInviteAsync(
            _tenantId,
            _creatorSubjectId,
            [TenantPermissions.MembersInvite],
            [_followerRoleId]);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Cannot grant*");
    }

    /// <summary>
    /// The guarded claim itself, reached only when the cap is hit after the invite was read: the
    /// row is taken to its cap out of band while the tracked entity still shows the earlier count,
    /// so <c>IsExhausted</c> passes and the UPDATE is what refuses. This is the shape of two
    /// concurrent accepts, which the sequential case below never reaches.
    /// </summary>
    [Fact]
    public async Task AcceptInviteAsync_whenTheCapIsReachedAfterTheInviteWasRead_isRefused()
    {
        await _service.CreateInviteAsync(
            _tenantId,
            _creatorSubjectId,
            OwnerPermissions,
            [_followerRoleId],
            maxUses: 1);

        // The tracked entity keeps use_count = 0, so the in-memory checks pass and only the
        // guarded UPDATE can catch this.
        await _dbContext.Database.ExecuteSqlRawAsync(
            "UPDATE member_invites SET use_count = 1");

        var result = await _service.AcceptInviteAsync(FakeToken, _acceptorSubjectId, _tenantId);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("exhausted");
        _tenantService.Verify(
            s => s.AddMemberAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<List<Guid>>(), It.IsAny<List<string>?>(),
                It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "the use is claimed before the membership is written, so a refused claim writes nothing");
    }

    /// <summary>
    /// The ordinary sequential case: a single-use invite is refused on its second acceptance.
    /// </summary>
    [Fact]
    public async Task AcceptInviteAsync_whenTheCapIsAlreadyReached_isRefused()
    {
        await _service.CreateInviteAsync(
            _tenantId,
            _creatorSubjectId,
            OwnerPermissions,
            [_followerRoleId],
            maxUses: 1);

        _tenantService
            .Setup(s => s.AddMemberAsync(
                _tenantId, It.IsAny<Guid>(), It.IsAny<List<Guid>>(), It.IsAny<List<string>?>(),
                It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, Guid subjectId, List<Guid> _, List<string>? _, string? _, bool _, CancellationToken _) =>
            {
                _dbContext.TenantMembers.Add(new TenantMemberEntity
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = _tenantId,
                    SubjectId = subjectId,
                });
                _dbContext.SaveChanges();
            })
            .Returns(Task.CompletedTask);

        var first = await _service.AcceptInviteAsync(FakeToken, _acceptorSubjectId, _tenantId);
        first.Success.Should().BeTrue();

        var second = await _service.AcceptInviteAsync(FakeToken, Guid.CreateVersion7(), _tenantId);

        second.Success.Should().BeFalse();
        second.ErrorCode.Should().Be("exhausted");
        (await _dbContext.MemberInvites.AsNoTracking().FirstAsync()).UseCount.Should().Be(1);
    }

    /// <summary>
    /// The token is a bearer credential for tenant membership, so its lifetime is bounded rather
    /// than taken from the caller.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3651)]
    public async Task CreateInviteAsync_withAnExpiryOutsideTheAllowedRange_isRefused(int expiresInDays)
    {
        var act = () => _service.CreateInviteAsync(
            _tenantId,
            _creatorSubjectId,
            OwnerPermissions,
            [_followerRoleId],
            expiresInDays: expiresInDays);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task AcceptInviteAsync_ExpiredToken_ReturnsError()
    {
        await _service.CreateInviteAsync(
            _tenantId,
            _creatorSubjectId,
            OwnerPermissions,
            [_followerRoleId]);

        var invite = await _dbContext.MemberInvites.FirstAsync();
        invite.ExpiresAt = DateTime.UtcNow.AddDays(-1);
        await _dbContext.SaveChangesAsync();

        var result = await _service.AcceptInviteAsync(FakeToken, _acceptorSubjectId, _tenantId);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("expired");
    }

    [Fact]
    public async Task AcceptInviteAsync_RevokedToken_ReturnsError()
    {
        await _service.CreateInviteAsync(
            _tenantId,
            _creatorSubjectId,
            OwnerPermissions,
            [_followerRoleId]);

        var invite = await _dbContext.MemberInvites.FirstAsync();
        invite.RevokedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync();

        var result = await _service.AcceptInviteAsync(FakeToken, _acceptorSubjectId, _tenantId);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("revoked");
    }

    [Fact]
    public async Task AcceptInviteAsync_ExhaustedUses_ReturnsError()
    {
        await _service.CreateInviteAsync(
            _tenantId,
            _creatorSubjectId,
            OwnerPermissions,
            [_followerRoleId],
            maxUses: 1);

        var invite = await _dbContext.MemberInvites.FirstAsync();
        invite.UseCount = 1;
        await _dbContext.SaveChangesAsync();

        var result = await _service.AcceptInviteAsync(FakeToken, _acceptorSubjectId, _tenantId);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("exhausted");
    }

    [Fact]
    public async Task AcceptInviteAsync_AlreadyMember_ReturnsError()
    {
        await _service.CreateInviteAsync(
            _tenantId,
            _creatorSubjectId,
            OwnerPermissions,
            [_followerRoleId]);

        // Add an existing active membership
        _dbContext.TenantMembers.Add(new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            SubjectId = _acceptorSubjectId,
            RevokedAt = null,
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.AcceptInviteAsync(FakeToken, _acceptorSubjectId, _tenantId);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("already_member");
    }

    /// <summary>
    /// An invite is only ever presented on the tenant it was minted for — the join page, the
    /// anonymous passkey signup and the accept endpoint all run on the tenant host and pass the
    /// tenant they resolved. A token from another tenant must therefore read as unknown, not as an
    /// invite that happens to point elsewhere: honouring it would join the caller to a tenant they
    /// never visited, on a host that never resolved it.
    /// </summary>
    [Fact]
    public async Task AcceptInviteAsync_whenTheTokenBelongsToAnotherTenant_isRefusedAsUnknown()
    {
        await _service.CreateInviteAsync(
            _tenantId,
            _creatorSubjectId,
            OwnerPermissions,
            [_followerRoleId]);

        var result = await _service.AcceptInviteAsync(
            FakeToken, _acceptorSubjectId, tenantId: Guid.CreateVersion7());

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("invalid_token");

        _tenantService.Verify(
            s => s.AddMemberAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<List<Guid>>(), It.IsAny<List<string>?>(),
                It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a token from another tenant must not write a membership anywhere");
    }

    /// <summary>
    /// The same boundary on the read side. The invite info feeds the join page and the anonymous
    /// passkey signup, which mints a subject before anyone has proved anything.
    /// </summary>
    [Fact]
    public async Task GetInviteByTokenAsync_whenTheTokenBelongsToAnotherTenant_returnsNull()
    {
        await _service.CreateInviteAsync(
            _tenantId,
            _creatorSubjectId,
            OwnerPermissions,
            [_followerRoleId]);

        (await _service.GetInviteByTokenAsync(FakeToken, Guid.CreateVersion7())).Should().BeNull();
        (await _service.GetInviteByTokenAsync(FakeToken, _tenantId)).Should().NotBeNull();
    }

    /// <summary>
    /// The other half of the tenant bound: on the invite's own tenant the acceptance still lands.
    /// </summary>
    [Fact]
    public async Task AcceptInviteAsync_onTheInvitesOwnTenant_addsTheMemberAndCountsTheUse()
    {
        await _service.CreateInviteAsync(
            _tenantId,
            _creatorSubjectId,
            OwnerPermissions,
            [_followerRoleId]);

        _tenantService
            .Setup(s => s.AddMemberAsync(
                _tenantId, _acceptorSubjectId, It.IsAny<List<Guid>>(), It.IsAny<List<string>?>(),
                It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                _dbContext.TenantMembers.Add(new TenantMemberEntity
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = _tenantId,
                    SubjectId = _acceptorSubjectId,
                });
                _dbContext.SaveChanges();
            })
            .Returns(Task.CompletedTask);

        var result = await _service.AcceptInviteAsync(FakeToken, _acceptorSubjectId, _tenantId);

        result.Success.Should().BeTrue();
        result.MembershipId.Should().NotBeNull();

        var invite = await _dbContext.MemberInvites.FirstAsync();
        invite.UseCount.Should().Be(1);
    }

    /// <summary>
    /// The tenant bound each call site writes by hand is now also a global query filter, so a
    /// query that forgets it still cannot reach another tenant's invite. SQLite enforces no
    /// PostgreSQL policy, so this pins the EF filter only; that the database refuses the same read
    /// is covered by the RLS integration tests, which assert every <c>ITenantScoped</c> table has
    /// RLS enabled, forced and policied.
    /// </summary>
    [Fact]
    public async Task MemberInvites_ofAnotherTenant_areNotReachableWithoutATenantPredicate()
    {
        var otherTenantId = Guid.CreateVersion7();
        _dbContext.Tenants.Add(new TenantEntity
        {
            Id = otherTenantId,
            Slug = "other",
            DisplayName = "Other Tenant",
        });
        _dbContext.MemberInvites.Add(new MemberInviteEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = otherTenantId,
            CreatedBySubjectId = _creatorSubjectId,
            TokenHash = "hash-minted-for-the-other-tenant",
            RoleIds = [],
            DirectPermissions = [TenantPermissions.GlucoseRead],
            ExpiresAt = DateTime.UtcNow.AddDays(7),
        });
        await _dbContext.SaveChangesAsync();

        // No tenant predicate: the filter is the only thing bounding this read.
        (await _dbContext.MemberInvites.ToListAsync()).Should().BeEmpty();

        (await _dbContext.MemberInvites.IgnoreQueryFilters().ToListAsync())
            .Should().ContainSingle("the row is present — it is the tenant filter that hides it");
    }

    [Fact]
    public async Task RevokeInviteAsync_SetsRevokedAt()
    {
        var createResult = await _service.CreateInviteAsync(
            _tenantId,
            _creatorSubjectId,
            OwnerPermissions,
            [_followerRoleId]);

        var result = await _service.RevokeInviteAsync(createResult.Id, _tenantId);

        result.Should().BeTrue();

        var invite = await _dbContext.MemberInvites.FirstAsync();
        invite.RevokedAt.Should().NotBeNull();
        invite.IsRevoked.Should().BeTrue();
    }
}

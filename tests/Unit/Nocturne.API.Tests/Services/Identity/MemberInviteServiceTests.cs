using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
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

    private const string FakeToken = "fake-random-token-abc123";
    private const string FakeTokenHash = "hashed-fake-token";
    private const string BaseUrl = "https://app.nocturnecgm.com";

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

        _jwtService = new Mock<IJwtService>();
        _jwtService.Setup(j => j.GenerateRefreshToken()).Returns(FakeToken);
        _jwtService.Setup(j => j.HashRefreshToken(FakeToken)).Returns(FakeTokenHash);

        _tenantService = new Mock<ITenantService>();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["BaseUrl"] = BaseUrl })
            .Build();

        var logger = new Mock<ILogger<MemberInviteService>>();

        _service = new MemberInviteService(
            _dbContext,
            _jwtService.Object,
            _tenantService.Object,
            configuration,
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
            Permissions = [TenantPermissions.GlucoseRead, TenantPermissions.StatisticsRead],
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
            [_followerRoleId]);

        result.Token.Should().Be(FakeToken);
        result.InviteUrl.Should().Be($"{BaseUrl}/invite/{FakeToken}");
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
            [],
            directPermissions: [TenantPermissions.GlucoseRead]);

        result.Token.Should().Be(FakeToken);

        var entity = await _dbContext.MemberInvites.FirstOrDefaultAsync();
        entity.Should().NotBeNull();
        entity!.DirectPermissions.Should().Contain(TenantPermissions.GlucoseRead);
    }

    [Fact]
    public async Task AcceptInviteAsync_ExpiredToken_ReturnsError()
    {
        await _service.CreateInviteAsync(
            _tenantId,
            _creatorSubjectId,
            [_followerRoleId]);

        var invite = await _dbContext.MemberInvites.FirstAsync();
        invite.ExpiresAt = DateTime.UtcNow.AddDays(-1);
        await _dbContext.SaveChangesAsync();

        var result = await _service.AcceptInviteAsync(FakeToken, _acceptorSubjectId);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("expired");
    }

    [Fact]
    public async Task AcceptInviteAsync_RevokedToken_ReturnsError()
    {
        await _service.CreateInviteAsync(
            _tenantId,
            _creatorSubjectId,
            [_followerRoleId]);

        var invite = await _dbContext.MemberInvites.FirstAsync();
        invite.RevokedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync();

        var result = await _service.AcceptInviteAsync(FakeToken, _acceptorSubjectId);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("revoked");
    }

    [Fact]
    public async Task AcceptInviteAsync_ExhaustedUses_ReturnsError()
    {
        await _service.CreateInviteAsync(
            _tenantId,
            _creatorSubjectId,
            [_followerRoleId],
            maxUses: 1);

        var invite = await _dbContext.MemberInvites.FirstAsync();
        invite.UseCount = 1;
        await _dbContext.SaveChangesAsync();

        var result = await _service.AcceptInviteAsync(FakeToken, _acceptorSubjectId);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("exhausted");
    }

    [Fact]
    public async Task AcceptInviteAsync_AlreadyMember_ReturnsError()
    {
        await _service.CreateInviteAsync(
            _tenantId,
            _creatorSubjectId,
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

        var result = await _service.AcceptInviteAsync(FakeToken, _acceptorSubjectId);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("already_member");
    }

    [Fact]
    public async Task RevokeInviteAsync_SetsRevokedAt()
    {
        var createResult = await _service.CreateInviteAsync(
            _tenantId,
            _creatorSubjectId,
            [_followerRoleId]);

        var result = await _service.RevokeInviteAsync(createResult.Id, _tenantId);

        result.Should().BeTrue();

        var invite = await _dbContext.MemberInvites.FirstAsync();
        invite.RevokedAt.Should().NotBeNull();
        invite.IsRevoked.Should().BeTrue();
    }
}

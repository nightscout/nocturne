using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Services.Auth;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>A code is live or expired by the injected clock alone.</summary>
[Trait("Category", "Unit")]
public class LoginCodeServiceClockTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2030, 6, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly LoginCodePurpose Purpose = LoginCodePurpose.Login;

    private readonly SqliteTestDatabase _db = TestDbContextFactory.CreateSqlite();
    private readonly FakeTimeProvider _time = new(Start);
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _subjectId = Guid.CreateVersion7();
    private readonly LoginCodeService _codes;

    public LoginCodeServiceClockTests()
    {
        var jwt = new Mock<IJwtService>();
        jwt.Setup(j => j.GenerateRefreshToken()).Returns(() => Guid.NewGuid().ToString("N"));
        _codes = new LoginCodeService(jwt.Object, new Mock<IAuthAuditService>().Object, _time);

        using var seed = _db.CreateContext(Guid.Empty);
        seed.Tenants.Add(new TenantEntity { Id = _tenantId, Slug = "alpha", DisplayName = "Alpha household" });
        seed.Subjects.Add(new SubjectEntity { Id = _subjectId, Name = "Rhys", IsActive = true });
        seed.TenantMembers.Add(new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(), TenantId = _tenantId, SubjectId = _subjectId,
        });
        seed.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private async Task<LoginCode> IssueAsync()
    {
        await using var db = _db.CreateContext(_tenantId);
        return await _codes.IssueAsync(db, _subjectId, Purpose, null, null, new AuthAuditActor(null, "test"));
    }

    private async Task<RedeemedLoginCode?> RedeemAsync(string code)
    {
        await using var db = _db.CreateContext(_tenantId);
        return await _codes.RedeemAsync(db, code, [Purpose], admits: null, withSpend: null, null, null);
    }

    private async Task<RedeemedLoginCode?> FindLiveAsync(string code)
    {
        await using var db = _db.CreateContext(_tenantId);
        return await _codes.FindLiveAsync(db, code, [Purpose]);
    }

    [Fact]
    public async Task IssueAsync_ExpiresTheCodeOneLifetimeAfterTheClock()
    {
        var issued = await IssueAsync();

        issued.ExpiresAt.Should().Be(Start.UtcDateTime + LoginCodeService.LifetimeOf(Purpose));
    }

    [Fact]
    public async Task RedeemAsync_RedeemsACodeUntilTheClockPassesItsExpiry()
    {
        var issued = await IssueAsync();

        _time.Advance(LoginCodeService.LifetimeOf(Purpose) - TimeSpan.FromSeconds(1));
        (await RedeemAsync(issued.Code)).Should().NotBeNull().And.Match<RedeemedLoginCode>(r => r.SubjectId == _subjectId);
    }

    [Fact]
    public async Task RedeemAsync_RefusesACodeOnceTheClockPassesItsExpiry()
    {
        var issued = await IssueAsync();

        _time.Advance(LoginCodeService.LifetimeOf(Purpose) + TimeSpan.FromSeconds(1));

        (await RedeemAsync(issued.Code)).Should().BeNull();
    }

    [Fact]
    public async Task FindLiveAsync_FindsACodeOnlyWhileTheClockIsBeforeItsExpiry()
    {
        var issued = await IssueAsync();
        (await FindLiveAsync(issued.Code)).Should().NotBeNull();

        _time.Advance(LoginCodeService.LifetimeOf(Purpose) + TimeSpan.FromSeconds(1));

        (await FindLiveAsync(issued.Code)).Should().BeNull();
    }

    [Fact]
    public async Task IssueAsync_PrunesACodeOnceTheClockPassesItsExpiry()
    {
        var first = await IssueAsync();
        _time.Advance(LoginCodeService.LifetimeOf(Purpose) + TimeSpan.FromSeconds(1));

        await IssueAsync();

        await using var db = _db.CreateContext(_tenantId);
        db.LoginCodes.Count(c => c.SubjectId == _subjectId).Should().Be(1, "the expired code is dropped");
        (await FindLiveAsync(first.Code)).Should().BeNull();
    }
}

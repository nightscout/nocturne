using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// A trusted device stands in for the authenticator code, so these pin who it stands in for, for
/// how long, and what ends it.
/// </summary>
[Trait("Category", "Unit")]
public class TrustedDeviceServiceTests : IDisposable
{
    private const string Chrome =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _dbContext;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 21, 9, 0, 0, TimeSpan.Zero));
    private readonly Guid _subjectId = Guid.CreateVersion7();
    private readonly Guid _otherSubjectId = Guid.CreateVersion7();

    public TrustedDeviceServiceTests()
    {
        _db = TestDbContextFactory.CreateSqlite();
        _dbContext = _db.CreateContext();
        _dbContext.Subjects.AddRange(
            new SubjectEntity { Id = _subjectId, Name = "Rhys" },
            new SubjectEntity { Id = _otherSubjectId, Name = "Erik" });
        _dbContext.SaveChanges();
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _db.Dispose();
    }

    private TrustedDeviceService CreateService(TimeSpan? lifetime = null) => new(
        new TrustedDeviceRepository(_dbContext.AsFactory()),
        Options.Create(new OidcOptions
        {
            Session = new SessionOptions { TrustedDeviceLifetime = lifetime ?? TimeSpan.FromDays(30) },
        }),
        _clock,
        NullLogger<TrustedDeviceService>.Instance);

    [Fact]
    public async Task ATrustedDevice_IsTrustedForTheSubjectThatTrustedIt()
    {
        var service = CreateService();

        var grant = await service.TrustAsync(_subjectId, replacedToken: null, Chrome, "203.0.113.7");

        grant.Should().NotBeNull();
        (await service.IsTrustedAsync(_subjectId, grant!.Token, "203.0.113.7")).Should().BeTrue();
    }

    [Fact]
    public async Task ATrustedDevice_IsNotTrustedForAnotherSubject()
    {
        var service = CreateService();
        var grant = await service.TrustAsync(_subjectId, null, Chrome, null);

        (await service.IsTrustedAsync(_otherSubjectId, grant!.Token, null))
            .Should().BeFalse("the token replaces the code only for the account that presented the code");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-token-this-service-minted")]
    public async Task AMissingOrUnknownToken_IsNotTrusted(string? token)
    {
        var service = CreateService();
        await service.TrustAsync(_subjectId, null, Chrome, null);

        (await service.IsTrustedAsync(_subjectId, token, null)).Should().BeFalse();
    }

    [Fact]
    public async Task TheStoreHoldsTheHashAndNotTheToken()
    {
        var service = CreateService();

        var grant = await service.TrustAsync(_subjectId, null, Chrome, null);

        var row = await _dbContext.TrustedDevices.AsNoTracking().SingleAsync();
        row.TokenHash.Should().NotBe(grant!.Token).And.HaveLength(64);
        row.DeviceDescription.Should().Be("Chrome on Windows");
    }

    [Fact]
    public async Task TheTrustEndsAfterItsLifetime_HoweverOftenItWasUsed()
    {
        var service = CreateService(TimeSpan.FromDays(30));
        var grant = await service.TrustAsync(_subjectId, null, Chrome, null);

        _clock.Advance(TimeSpan.FromDays(29));
        (await service.IsTrustedAsync(_subjectId, grant!.Token, null)).Should().BeTrue();

        _clock.Advance(TimeSpan.FromDays(1));
        (await service.IsTrustedAsync(_subjectId, grant.Token, null))
            .Should().BeFalse("using the trust does not extend it");
        (await service.ListAsync(_subjectId, grant.Token)).Should().BeEmpty();
    }

    [Fact]
    public async Task UsingTheTrust_RecordsWhenAndFromWhere()
    {
        var service = CreateService();
        var grant = await service.TrustAsync(_subjectId, null, Chrome, "203.0.113.7");

        _clock.Advance(TimeSpan.FromDays(3));
        await service.IsTrustedAsync(_subjectId, grant!.Token, "198.51.100.4");

        var device = (await service.ListAsync(_subjectId, grant.Token)).Should().ContainSingle().Subject;
        device.LastUsedAt.Should().Be(_clock.GetUtcNow().UtcDateTime);
        device.IpAddress.Should().Be("198.51.100.4");
        device.IsCurrent.Should().BeTrue();
    }

    [Fact]
    public async Task TrustingADeviceAgain_ReplacesItsRow()
    {
        var service = CreateService();
        var first = await service.TrustAsync(_subjectId, null, Chrome, null);

        var second = await service.TrustAsync(_subjectId, first!.Token, Chrome, null);

        (await service.ListAsync(_subjectId, second!.Token)).Should().ContainSingle()
            .Which.Id.Should().Be(second.DeviceId);
        (await service.IsTrustedAsync(_subjectId, first.Token, null)).Should().BeFalse();
    }

    [Fact]
    public async Task TrustingADevice_DoesNotRemoveAnotherSubjectsRowForTheSameCookie()
    {
        var service = CreateService();
        var others = await service.TrustAsync(_otherSubjectId, null, Chrome, null);

        await service.TrustAsync(_subjectId, others!.Token, Chrome, null);

        (await service.IsTrustedAsync(_otherSubjectId, others.Token, null)).Should().BeTrue();
    }

    [Fact]
    public async Task ARevokedDevice_IsNotTrusted()
    {
        var service = CreateService();
        var kept = await service.TrustAsync(_subjectId, null, Chrome, null);
        var revoked = await service.TrustAsync(_subjectId, null, Chrome, null);

        (await service.RevokeAsync(revoked!.DeviceId, _subjectId)).Should().BeTrue();

        (await service.IsTrustedAsync(_subjectId, revoked.Token, null)).Should().BeFalse();
        (await service.IsTrustedAsync(_subjectId, kept!.Token, null)).Should().BeTrue();
    }

    [Fact]
    public async Task ASubjectCannotRevokeAnotherSubjectsDevice()
    {
        var service = CreateService();
        var grant = await service.TrustAsync(_subjectId, null, Chrome, null);

        (await service.RevokeAsync(grant!.DeviceId, _otherSubjectId)).Should().BeFalse();

        (await service.IsTrustedAsync(_subjectId, grant.Token, null)).Should().BeTrue();
    }

    [Fact]
    public async Task RevokingAll_EndsEveryDeviceOfThatSubjectOnly()
    {
        var service = CreateService();
        var mine = await service.TrustAsync(_subjectId, null, Chrome, null);
        await service.TrustAsync(_subjectId, null, Chrome, null);
        var others = await service.TrustAsync(_otherSubjectId, null, Chrome, null);

        (await service.RevokeAllAsync(_subjectId)).Should().Be(2);

        (await service.IsTrustedAsync(_subjectId, mine!.Token, null)).Should().BeFalse();
        (await service.IsTrustedAsync(_otherSubjectId, others!.Token, null)).Should().BeTrue();
    }

    [Fact]
    public async Task WhenTurnedOff_NothingIsTrustedAndNothingAlreadyTrustedIsHonoured()
    {
        var grant = await CreateService().TrustAsync(_subjectId, null, Chrome, null);
        var off = CreateService(TimeSpan.Zero);

        (await off.TrustAsync(_subjectId, null, Chrome, null)).Should().BeNull();
        (await off.IsTrustedAsync(_subjectId, grant!.Token, null)).Should().BeFalse();
        (await off.ListAsync(_subjectId, grant.Token)).Should().BeEmpty();
    }

    [Theory]
    [InlineData(Chrome, "Chrome on Windows")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36 Edg/140.0.0.0", "Edge on Windows")]
    [InlineData("Mozilla/5.0 (Linux; Android 15; Pixel 9) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Mobile Safari/537.36", "Chrome on Android")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1", "Safari on iPhone")]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10.15; rv:131.0) Gecko/20100101 Firefox/131.0", "Firefox on Mac")]
    [InlineData("Mozilla/5.0 (X11; Linux x86_64; rv:131.0) Gecko/20100101 Firefox/131.0", "Firefox on Linux")]
    [InlineData("curl/8.9.0", "curl/8.9.0")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void DeviceLabel_NamesTheBrowserAndThePlatform(string? userAgent, string? expected) =>
        DeviceLabel.FromUserAgent(userAgent).Should().Be(expected);
}

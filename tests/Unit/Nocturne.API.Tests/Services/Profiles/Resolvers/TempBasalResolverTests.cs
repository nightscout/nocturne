using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Profiles.Resolvers;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Services.Profiles.Resolvers;

public class TempBasalResolverTests
{
    private readonly Mock<IBasalRateResolver> _basalRateResolver = new();
    private readonly Mock<ITempBasalRepository> _tempBasalRepo = new();
    private readonly TempBasalResolver _sut;

    // 2024-01-15 12:00:00 UTC
    private const long NoonMills = 1705320000000;
    private static readonly DateTime NoonUtc = DateTimeOffset.FromUnixTimeMilliseconds(NoonMills).UtcDateTime;

    public TempBasalResolverTests()
    {
        _sut = new TempBasalResolver(
            _basalRateResolver.Object,
            _tempBasalRepo.Object,
            NullLogger<TempBasalResolver>.Instance);
    }

    [Fact]
    public async Task ReturnsScheduledBasalOnly_WhenNoTempBasalActive()
    {
        _basalRateResolver.Setup(r => r.GetBasalRateAsync(NoonMills, null, default))
            .ReturnsAsync(1.2);
        _tempBasalRepo.Setup(r => r.GetAsync(null, It.IsAny<DateTime>(), null, null, 10, 0, true, default))
            .ReturnsAsync(Enumerable.Empty<TempBasal>());

        var result = await _sut.GetTempBasalAsync(NoonMills);

        result.Basal.Should().Be(1.2);
        result.TempBasal.Should().BeNull();
        result.ComboBolusBasal.Should().BeNull();
        result.TotalBasal.Should().Be(1.2);
    }

    [Fact]
    public async Task ReturnsActiveTempBasal_WhenOverrideIsActive()
    {
        _basalRateResolver.Setup(r => r.GetBasalRateAsync(NoonMills, null, default))
            .ReturnsAsync(1.0);
        var tempBasal = new TempBasal
        {
            Id = Guid.NewGuid(),
            StartTimestamp = NoonUtc.AddMinutes(-30),
            EndTimestamp = NoonUtc.AddMinutes(30),
            Rate = 2.5,
        };
        _tempBasalRepo.Setup(r => r.GetAsync(null, It.IsAny<DateTime>(), null, null, 10, 0, true, default))
            .ReturnsAsync(new[] { tempBasal });

        var result = await _sut.GetTempBasalAsync(NoonMills);

        result.Basal.Should().Be(1.0);
        result.TempBasal.Should().Be(2.5);
        result.TotalBasal.Should().Be(2.5);
    }

    [Fact]
    public async Task IgnoresExpiredTempBasal()
    {
        _basalRateResolver.Setup(r => r.GetBasalRateAsync(NoonMills, null, default))
            .ReturnsAsync(1.0);
        var expiredTempBasal = new TempBasal
        {
            Id = Guid.NewGuid(),
            StartTimestamp = NoonUtc.AddHours(-2),
            EndTimestamp = NoonUtc.AddHours(-1),
            Rate = 3.0,
        };
        _tempBasalRepo.Setup(r => r.GetAsync(null, It.IsAny<DateTime>(), null, null, 10, 0, true, default))
            .ReturnsAsync(new[] { expiredTempBasal });

        var result = await _sut.GetTempBasalAsync(NoonMills);

        result.TempBasal.Should().BeNull();
        result.TotalBasal.Should().Be(1.0);
    }

    [Fact]
    public async Task PassesSpecProfileToBasalRateResolver()
    {
        _basalRateResolver.Setup(r => r.GetBasalRateAsync(NoonMills, "Custom", default))
            .ReturnsAsync(2.0);
        _tempBasalRepo.Setup(r => r.GetAsync(null, It.IsAny<DateTime>(), null, null, 10, 0, true, default))
            .ReturnsAsync(Enumerable.Empty<TempBasal>());

        var result = await _sut.GetTempBasalAsync(NoonMills, specProfile: "Custom");

        result.Basal.Should().Be(2.0);
        _basalRateResolver.Verify(r => r.GetBasalRateAsync(NoonMills, "Custom", default), Times.Once);
    }

    [Fact]
    public async Task SelectsMostRecentTempBasal_WhenMultipleOverlap()
    {
        _basalRateResolver.Setup(r => r.GetBasalRateAsync(NoonMills, null, default))
            .ReturnsAsync(1.0);
        var older = new TempBasal
        {
            Id = Guid.NewGuid(),
            StartTimestamp = NoonUtc.AddHours(-1),
            EndTimestamp = NoonUtc.AddHours(1),
            Rate = 1.5,
        };
        var newer = new TempBasal
        {
            Id = Guid.NewGuid(),
            StartTimestamp = NoonUtc.AddMinutes(-15),
            EndTimestamp = NoonUtc.AddMinutes(45),
            Rate = 2.0,
        };
        _tempBasalRepo.Setup(r => r.GetAsync(null, It.IsAny<DateTime>(), null, null, 10, 0, true, default))
            .ReturnsAsync(new[] { newer, older });

        var result = await _sut.GetTempBasalAsync(NoonMills);

        result.TempBasal.Should().Be(2.0);
    }
}

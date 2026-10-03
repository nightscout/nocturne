using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Profiles.Resolvers;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Services.Profiles.Resolvers;

public class TherapySettingsResolverTests : IDisposable
{
    private readonly Mock<ITherapySettingsRepository> _repo = new();
    private readonly Mock<IPatientInsulinRepository> _insulinRepo = new();
    private readonly Mock<IPatientRecordRepository> _patientRecordRepo = new();
    private readonly Mock<IActiveProfileResolver> _activeProfileResolver = new();
    private readonly Mock<ITenantAccessor> _tenantAccessor = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly TherapySettingsResolver _sut;

    private static readonly Guid TenantId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private const long NoonMills = 1705320000000;

    public TherapySettingsResolverTests()
    {
        _tenantAccessor.Setup(t => t.TenantId).Returns(TenantId);

        _sut = new TherapySettingsResolver(
            _repo.Object,
            _insulinRepo.Object,
            _patientRecordRepo.Object,
            _activeProfileResolver.Object,
            _tenantAccessor.Object,
            _cache,
            NullLogger<TherapySettingsResolver>.Instance);
    }

    public void Dispose() => _cache.Dispose();

    private static TherapySettings MakeSettings(
        double dia = 5.0,
        int carbsHr = 25,
        string? timezone = "America/New_York",
        string? units = "mg/dL",
        bool isExternallyManaged = false) => new()
    {
        Id = Guid.NewGuid(),
        ProfileName = "Default",
        Dia = dia,
        CarbsHr = carbsHr,
        Timezone = timezone,
        Units = units,
        IsExternallyManaged = isExternallyManaged,
    };

    [Fact]
    public async Task GetDIA_ReturnsPatientInsulinDia_WhenNotExternallyManaged()
    {
        var settings = MakeSettings(dia: 5.0);
        _repo.Setup(r => r.GetActiveAtAsync("Default", It.IsAny<DateTime>(), default))
            .ReturnsAsync(settings);
        _insulinRepo.Setup(r => r.GetPrimaryBolusInsulinAsync(default))
            .ReturnsAsync(new PatientInsulin { Dia = 4.5, IsPrimary = true });

        var result = await _sut.GetDIAAsync(NoonMills);

        result.Should().Be(4.5);
    }

    [Fact]
    public async Task GetDIA_ReturnsTherapySettingsDia_WhenExternallyManaged()
    {
        var settings = MakeSettings(dia: 6.0, isExternallyManaged: true);
        _repo.Setup(r => r.GetActiveAtAsync("Default", It.IsAny<DateTime>(), default))
            .ReturnsAsync(settings);

        var result = await _sut.GetDIAAsync(NoonMills);

        result.Should().Be(6.0);
        _insulinRepo.Verify(r => r.GetPrimaryBolusInsulinAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetDIA_FallsBackToTherapySettings_WhenNoPatientInsulin()
    {
        var settings = MakeSettings(dia: 5.0);
        _repo.Setup(r => r.GetActiveAtAsync("Default", It.IsAny<DateTime>(), default))
            .ReturnsAsync(settings);
        _insulinRepo.Setup(r => r.GetPrimaryBolusInsulinAsync(default))
            .ReturnsAsync((PatientInsulin?)null);

        var result = await _sut.GetDIAAsync(NoonMills);

        result.Should().Be(5.0);
    }

    [Fact]
    public async Task GetDIA_ReturnsDefault_WhenNoSettingsExist()
    {
        _repo.Setup(r => r.GetActiveAtAsync("Default", It.IsAny<DateTime>(), default))
            .ReturnsAsync((TherapySettings?)null);

        var result = await _sut.GetDIAAsync(NoonMills);

        result.Should().Be(3.0);
    }

    [Theory]
    [InlineData(false, 5.0, 4.5, InsulinActionTimeSource.PrimaryInsulin, 4.5)]
    [InlineData(true, 6.0, 4.5, InsulinActionTimeSource.ExternalProfile, 6.0)]
    [InlineData(false, 5.0, null, InsulinActionTimeSource.Profile, 5.0)]
    [InlineData(false, 0.0, null, InsulinActionTimeSource.Default, 3.0)]
    public async Task GetActionTime_NamesTheSourceGetDIAUses(
        bool external, double profileDia, double? insulinDia, InsulinActionTimeSource source, double hours)
    {
        _repo.Setup(r => r.GetActiveAtAsync("Default", It.IsAny<DateTime>(), default))
            .ReturnsAsync(MakeSettings(dia: profileDia, isExternallyManaged: external));
        _insulinRepo.Setup(r => r.GetPrimaryBolusInsulinAsync(default))
            .ReturnsAsync(insulinDia is { } dia ? new PatientInsulin { Name = "Fiasp", Dia = dia, IsPrimary = true } : null);

        var actionTime = await _sut.GetActionTimeAsync(NoonMills);

        actionTime.Should().Be(new InsulinActionTime(source, hours, insulinDia is null ? null : "Fiasp"));
        (await _sut.GetDIAAsync(NoonMills)).Should().Be(hours);
    }

    [Fact]
    public async Task GetActionTime_WithNoProfile_IsTheDefault_EvenWithAnInsulinPicked()
    {
        _repo.Setup(r => r.GetActiveAtAsync("Default", It.IsAny<DateTime>(), default))
            .ReturnsAsync((TherapySettings?)null);
        _insulinRepo.Setup(r => r.GetPrimaryBolusInsulinAsync(default))
            .ReturnsAsync(new PatientInsulin { Name = "Fiasp", Dia = 3.5, IsPrimary = true });

        (await _sut.GetActionTimeAsync(NoonMills))
            .Should().Be(new InsulinActionTime(InsulinActionTimeSource.Default, 3.0, "Fiasp"));
    }

    [Fact]
    public async Task GetCarbAbsorptionRate_ReturnsValue()
    {
        var settings = MakeSettings(carbsHr: 30);
        _repo.Setup(r => r.GetActiveAtAsync("Default", It.IsAny<DateTime>(), default))
            .ReturnsAsync(settings);

        var result = await _sut.GetCarbAbsorptionRateAsync(NoonMills);

        result.Should().Be(30.0);
    }

    [Fact]
    public async Task GetCarbAbsorptionRate_ReturnsDefault_WhenNoSettings()
    {
        _repo.Setup(r => r.GetActiveAtAsync("Default", It.IsAny<DateTime>(), default))
            .ReturnsAsync((TherapySettings?)null);

        var result = await _sut.GetCarbAbsorptionRateAsync(NoonMills);

        result.Should().Be(20.0);
    }

    [Fact]
    public async Task GetTimezone_PrefersPatientRecord_OverTherapySettings()
    {
        // PatientRecord is the canonical source. When set, the resolver must use it
        // regardless of any legacy value living on the active TherapySettings record.
        _patientRecordRepo.Setup(r => r.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PatientRecord { Timezone = "Australia/Sydney" });
        _repo.Setup(r => r.GetActiveAtAsync(It.IsAny<string>(), It.IsAny<DateTime>(), default))
            .ReturnsAsync(MakeSettings(timezone: "Europe/London"));

        var result = await _sut.GetTimezoneAsync();

        result.Should().Be("Australia/Sydney");
    }

    [Fact]
    public async Task GetTimezone_FallsBackToTherapySettings_WhenPatientRecordHasNoTimezone()
    {
        // Legacy / connector-imported tenants may still have therapy_settings.timezone set
        // but no patient_records.timezone backfilled. Treat the per-profile value as a
        // fallback so existing data keeps working until backfill / next save.
        _patientRecordRepo.Setup(r => r.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PatientRecord { Timezone = null });
        _activeProfileResolver.Setup(r => r.GetActiveProfileNameAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("A");
        _repo.Setup(r => r.GetActiveAtAsync("A", It.IsAny<DateTime>(), default))
            .ReturnsAsync(MakeSettings(timezone: "Europe/London"));

        var result = await _sut.GetTimezoneAsync();

        result.Should().Be("Europe/London");
    }

    [Fact]
    public async Task GetTimezone_UsesActiveProfileForLegacyFallback_NotHardcodedDefault()
    {
        // Bug regression: previous implementation hardcoded the profile lookup to "Default",
        // so a tenant whose only profiles are e.g. "A" and "B" got null even when "A" had a
        // timezone. The fallback path must consult the active profile.
        _patientRecordRepo.Setup(r => r.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PatientRecord { Timezone = null });
        _activeProfileResolver.Setup(r => r.GetActiveProfileNameAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("A");
        _repo.Setup(r => r.GetActiveAtAsync("Default", It.IsAny<DateTime>(), default))
            .ReturnsAsync((TherapySettings?)null);
        _repo.Setup(r => r.GetActiveAtAsync("A", It.IsAny<DateTime>(), default))
            .ReturnsAsync(MakeSettings(timezone: "Pacific/Auckland"));

        var result = await _sut.GetTimezoneAsync();

        result.Should().Be("Pacific/Auckland");
    }

    [Fact]
    public async Task GetTimezone_ReturnsNull_WhenNeitherSourceHasTimezone()
    {
        _patientRecordRepo.Setup(r => r.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((PatientRecord?)null);
        _activeProfileResolver.Setup(r => r.GetActiveProfileNameAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        _repo.Setup(r => r.GetActiveAtAsync(It.IsAny<string>(), It.IsAny<DateTime>(), default))
            .ReturnsAsync((TherapySettings?)null);

        var result = await _sut.GetTimezoneAsync();

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetUnits_ReturnsUnits()
    {
        var settings = MakeSettings(units: "mmol/L");
        _repo.Setup(r => r.GetActiveAtAsync("Default", It.IsAny<DateTime>(), default))
            .ReturnsAsync(settings);

        var result = await _sut.GetUnitsAsync();

        result.Should().Be("mmol/L");
    }

    [Fact]
    public async Task HasData_ReturnsTrue_WhenRecordsExist()
    {
        _repo.Setup(r => r.CountAsync(null, null, default)).ReturnsAsync(5);

        var result = await _sut.HasDataAsync();

        result.Should().BeTrue();
    }

    [Fact]
    public async Task HasData_ReturnsFalse_WhenNoRecords()
    {
        _repo.Setup(r => r.CountAsync(null, null, default)).ReturnsAsync(0);

        var result = await _sut.HasDataAsync();

        result.Should().BeFalse();
    }
}

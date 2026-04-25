using FluentAssertions;
using Moq;
using Nocturne.API.Services.Profiles;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Profiles;

public class ProfileProjectionServiceTests
{
    private readonly Mock<ITherapySettingsRepository> _therapyRepo = new();
    private readonly Mock<IBasalScheduleRepository> _basalRepo = new();
    private readonly Mock<ICarbRatioScheduleRepository> _carbRatioRepo = new();
    private readonly Mock<ISensitivityScheduleRepository> _sensitivityRepo = new();
    private readonly Mock<ITargetRangeScheduleRepository> _targetRangeRepo = new();
    private readonly ProfileProjectionService _sut;

    public ProfileProjectionServiceTests()
    {
        _sut = new ProfileProjectionService(
            _therapyRepo.Object,
            _basalRepo.Object,
            _carbRatioRepo.Object,
            _sensitivityRepo.Object,
            _targetRangeRepo.Object);
    }

    #region GetCurrentProfileAsync

    [Fact]
    public async Task GetCurrentProfileAsync_NoSettings_ReturnsNull()
    {
        _therapyRepo.Setup(r => r.GetAsync(null, null, null, null, 1, 0, true, default))
            .ReturnsAsync(Enumerable.Empty<TherapySettings>());

        var result = await _sut.GetCurrentProfileAsync();

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetCurrentProfileAsync_ReturnsLatestProfile()
    {
        var correlationId = Guid.NewGuid();
        var settings = CreateTherapySettings(correlationId: correlationId, profileName: "Default");
        var basal = CreateBasalSchedule(correlationId, "Default");
        var carbRatio = CreateCarbRatioSchedule(correlationId, "Default");
        var sensitivity = CreateSensitivitySchedule(correlationId, "Default");
        var targetRange = CreateTargetRangeSchedule(correlationId, "Default");

        SetupGetLatest(settings);
        SetupCorrelationLookups(correlationId, [basal], [carbRatio], [sensitivity], [targetRange]);

        var result = await _sut.GetCurrentProfileAsync();

        result.Should().NotBeNull();
        result!.DefaultProfile.Should().Be("Default");
        result.Mills.Should().Be(settings.Mills);
        result.Store.Should().ContainKey("Default");

        var data = result.Store["Default"];
        data.Basal.Should().HaveCount(1);
        data.CarbRatio.Should().HaveCount(1);
        data.Sens.Should().HaveCount(1);
        data.TargetLow.Should().HaveCount(1);
        data.TargetHigh.Should().HaveCount(1);
    }

    #endregion

    #region GetProfileByIdAsync

    [Fact]
    public async Task GetProfileByIdAsync_ByLegacyId_ReturnsProfile()
    {
        var correlationId = Guid.NewGuid();
        var settings = CreateTherapySettings(correlationId: correlationId, legacyId: "abc123:Default");

        _therapyRepo.Setup(r => r.GetByLegacyIdAsync("abc123", default))
            .ReturnsAsync(settings);
        SetupCorrelationLookups(correlationId);

        var result = await _sut.GetProfileByIdAsync("abc123");

        result.Should().NotBeNull();
        result!.Id.Should().Be("abc123");
    }

    [Fact]
    public async Task GetProfileByIdAsync_ByGuid_ReturnsProfile()
    {
        var id = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var settings = CreateTherapySettings(id: id, correlationId: correlationId);

        _therapyRepo.Setup(r => r.GetByLegacyIdAsync(id.ToString(), default))
            .ReturnsAsync((TherapySettings?)null);
        _therapyRepo.Setup(r => r.GetByIdAsync(id, default))
            .ReturnsAsync(settings);
        SetupCorrelationLookups(correlationId);

        var result = await _sut.GetProfileByIdAsync(id.ToString());

        result.Should().NotBeNull();
    }

    [Fact]
    public async Task GetProfileByIdAsync_NotFound_ReturnsNull()
    {
        _therapyRepo.Setup(r => r.GetByLegacyIdAsync("missing", default))
            .ReturnsAsync((TherapySettings?)null);

        var result = await _sut.GetProfileByIdAsync("missing");

        result.Should().BeNull();
    }

    #endregion

    #region GetProfilesAsync

    [Fact]
    public async Task GetProfilesAsync_ReturnsPaginatedProfiles()
    {
        var corr1 = Guid.NewGuid();
        var corr2 = Guid.NewGuid();
        var settings1 = CreateTherapySettings(correlationId: corr1, profileName: "Default");
        var settings2 = CreateTherapySettings(correlationId: corr2, profileName: "Weekday");

        _therapyRepo.Setup(r => r.GetAsync(null, null, null, null, 5, 0, true, default))
            .ReturnsAsync([settings1, settings2]);
        SetupCorrelationLookups(corr1);
        SetupCorrelationLookups(corr2);

        var result = (await _sut.GetProfilesAsync(count: 5, skip: 0)).ToList();

        result.Should().HaveCount(2);
        result[0].DefaultProfile.Should().Be("Default");
        result[1].DefaultProfile.Should().Be("Weekday");
    }

    [Fact]
    public async Task GetProfilesAsync_EmptyRepo_ReturnsEmpty()
    {
        _therapyRepo.Setup(r => r.GetAsync(null, null, null, null, 10, 0, true, default))
            .ReturnsAsync(Enumerable.Empty<TherapySettings>());

        var result = await _sut.GetProfilesAsync();

        result.Should().BeEmpty();
    }

    #endregion

    #region CountProfilesAsync

    [Fact]
    public async Task CountProfilesAsync_DelegatesToRepo()
    {
        _therapyRepo.Setup(r => r.CountAsync(null, null, default)).ReturnsAsync(42);

        var result = await _sut.CountProfilesAsync();

        result.Should().Be(42);
    }

    #endregion

    #region Profile Assembly

    [Fact]
    public async Task AssemblesScalarFieldsFromTherapySettings()
    {
        var correlationId = Guid.NewGuid();
        var settings = CreateTherapySettings(
            correlationId: correlationId,
            profileName: "NightMode",
            timezone: "America/New_York",
            units: "mmol/L",
            dia: 4.5,
            carbsHr: 30,
            delay: 15,
            enteredBy: "Loop",
            isExternallyManaged: true,
            startDate: "2025-01-15T00:00:00.000Z");

        SetupGetLatest(settings);
        SetupCorrelationLookups(correlationId);

        var result = await _sut.GetCurrentProfileAsync();

        result.Should().NotBeNull();
        result!.DefaultProfile.Should().Be("NightMode");
        result.Units.Should().Be("mmol/L");
        result.EnteredBy.Should().Be("Loop");
        result.IsExternallyManaged.Should().BeTrue();
        result.StartDate.Should().Be("2025-01-15T00:00:00.000Z");

        var data = result.Store["NightMode"];
        data.Dia.Should().Be(4.5);
        data.CarbsHr.Should().Be(30);
        data.Delay.Should().Be(15);
        data.Timezone.Should().Be("America/New_York");
        data.Units.Should().Be("mmol/L");
    }

    [Fact]
    public async Task TargetRange_SplitsIntoTargetLowAndTargetHigh()
    {
        var correlationId = Guid.NewGuid();
        var settings = CreateTherapySettings(correlationId: correlationId);
        var targetRange = new TargetRangeSchedule
        {
            Id = Guid.NewGuid(),
            CorrelationId = correlationId,
            ProfileName = "Default",
            Entries =
            [
                new TargetRangeEntry { Time = "00:00", Low = 80, High = 120, TimeAsSeconds = 0 },
                new TargetRangeEntry { Time = "06:00", Low = 90, High = 130, TimeAsSeconds = 21600 },
                new TargetRangeEntry { Time = "22:00", Low = 70, High = 110, TimeAsSeconds = 79200 },
            ]
        };

        SetupGetLatest(settings);
        SetupCorrelationLookups(correlationId, targetRanges: [targetRange]);

        var result = await _sut.GetCurrentProfileAsync();

        var data = result!.Store["Default"];

        data.TargetLow.Should().HaveCount(3);
        data.TargetLow[0].Time.Should().Be("00:00");
        data.TargetLow[0].Value.Should().Be(80);
        data.TargetLow[0].TimeAsSeconds.Should().Be(0);
        data.TargetLow[1].Value.Should().Be(90);
        data.TargetLow[2].Value.Should().Be(70);

        data.TargetHigh.Should().HaveCount(3);
        data.TargetHigh[0].Value.Should().Be(120);
        data.TargetHigh[1].Value.Should().Be(130);
        data.TargetHigh[2].Value.Should().Be(110);
    }

    [Fact]
    public async Task ScheduleEntries_MapToTimeValues()
    {
        var correlationId = Guid.NewGuid();
        var settings = CreateTherapySettings(correlationId: correlationId);
        var basal = new BasalSchedule
        {
            Id = Guid.NewGuid(),
            CorrelationId = correlationId,
            ProfileName = "Default",
            Entries =
            [
                new ScheduleEntry { Time = "00:00", Value = 0.8, TimeAsSeconds = 0 },
                new ScheduleEntry { Time = "08:00", Value = 1.2, TimeAsSeconds = 28800 },
            ]
        };

        SetupGetLatest(settings);
        SetupCorrelationLookups(correlationId, basals: [basal]);

        var result = await _sut.GetCurrentProfileAsync();

        var data = result!.Store["Default"];
        data.Basal.Should().HaveCount(2);
        data.Basal[0].Time.Should().Be("00:00");
        data.Basal[0].Value.Should().Be(0.8);
        data.Basal[0].TimeAsSeconds.Should().Be(0);
        data.Basal[1].Time.Should().Be("08:00");
        data.Basal[1].Value.Should().Be(1.2);
        data.Basal[1].TimeAsSeconds.Should().Be(28800);
    }

    [Fact]
    public async Task MultipleNamedProfiles_EachGetOwnStoreKey()
    {
        var corr1 = Guid.NewGuid();
        var corr2 = Guid.NewGuid();
        var settings1 = CreateTherapySettings(correlationId: corr1, profileName: "Default");
        var settings2 = CreateTherapySettings(correlationId: corr2, profileName: "Exercise");

        _therapyRepo.Setup(r => r.GetAsync(null, null, null, null, 10, 0, true, default))
            .ReturnsAsync([settings1, settings2]);
        SetupCorrelationLookups(corr1);
        SetupCorrelationLookups(corr2);

        var result = (await _sut.GetProfilesAsync()).ToList();

        result[0].Store.Should().ContainKey("Default");
        result[0].Store.Should().NotContainKey("Exercise");
        result[1].Store.Should().ContainKey("Exercise");
        result[1].Store.Should().NotContainKey("Default");
    }

    [Fact]
    public async Task MissingSchedules_ProducesEmptyLists()
    {
        var correlationId = Guid.NewGuid();
        var settings = CreateTherapySettings(correlationId: correlationId);

        SetupGetLatest(settings);
        // Set up correlation lookups that return no matching profile name
        _basalRepo.Setup(r => r.GetByCorrelationIdAsync(correlationId, default))
            .ReturnsAsync(Enumerable.Empty<BasalSchedule>());
        _carbRatioRepo.Setup(r => r.GetByCorrelationIdAsync(correlationId, default))
            .ReturnsAsync(Enumerable.Empty<CarbRatioSchedule>());
        _sensitivityRepo.Setup(r => r.GetByCorrelationIdAsync(correlationId, default))
            .ReturnsAsync(Enumerable.Empty<SensitivitySchedule>());
        _targetRangeRepo.Setup(r => r.GetByCorrelationIdAsync(correlationId, default))
            .ReturnsAsync(Enumerable.Empty<TargetRangeSchedule>());

        var result = await _sut.GetCurrentProfileAsync();

        var data = result!.Store["Default"];
        data.Basal.Should().BeEmpty();
        data.CarbRatio.Should().BeEmpty();
        data.Sens.Should().BeEmpty();
        data.TargetLow.Should().BeEmpty();
        data.TargetHigh.Should().BeEmpty();
    }

    [Fact]
    public async Task NoCorrelationId_FallsBackToProfileNameLookup()
    {
        var settings = CreateTherapySettings(correlationId: null, profileName: "Default");
        var basal = CreateBasalSchedule(null, "Default");

        SetupGetLatest(settings);

        _basalRepo.Setup(r => r.GetByProfileNameAsync("Default", default))
            .ReturnsAsync(new[] { basal });
        _carbRatioRepo.Setup(r => r.GetByProfileNameAsync("Default", default))
            .ReturnsAsync(Enumerable.Empty<CarbRatioSchedule>());
        _sensitivityRepo.Setup(r => r.GetByProfileNameAsync("Default", default))
            .ReturnsAsync(Enumerable.Empty<SensitivitySchedule>());
        _targetRangeRepo.Setup(r => r.GetByProfileNameAsync("Default", default))
            .ReturnsAsync(Enumerable.Empty<TargetRangeSchedule>());

        var result = await _sut.GetCurrentProfileAsync();

        result.Should().NotBeNull();
        result!.Store["Default"].Basal.Should().HaveCount(1);

        _basalRepo.Verify(r => r.GetByProfileNameAsync("Default", default), Times.Once);
    }

    [Fact]
    public async Task LegacyId_ExtractsProfileIdPrefix()
    {
        var correlationId = Guid.NewGuid();
        var settings = CreateTherapySettings(
            correlationId: correlationId,
            legacyId: "507f1f77bcf86cd799439011:Default");

        _therapyRepo.Setup(r => r.GetByLegacyIdAsync("507f1f77bcf86cd799439011", default))
            .ReturnsAsync(settings);
        SetupCorrelationLookups(correlationId);

        var result = await _sut.GetProfileByIdAsync("507f1f77bcf86cd799439011");

        result.Should().NotBeNull();
        result!.Id.Should().Be("507f1f77bcf86cd799439011");
    }

    #endregion

    #region Helpers

    private static TherapySettings CreateTherapySettings(
        Guid? id = null,
        Guid? correlationId = null,
        string profileName = "Default",
        string? legacyId = null,
        string? timezone = null,
        string? units = null,
        double dia = 3.0,
        int carbsHr = 20,
        int delay = 20,
        string? enteredBy = null,
        bool isExternallyManaged = false,
        string? startDate = null)
    {
        return new TherapySettings
        {
            Id = id ?? Guid.NewGuid(),
            CorrelationId = correlationId,
            ProfileName = profileName,
            LegacyId = legacyId,
            Timestamp = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc),
            CreatedAt = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc),
            ModifiedAt = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc),
            Timezone = timezone,
            Units = units ?? "mg/dL",
            Dia = dia,
            CarbsHr = carbsHr,
            Delay = delay,
            EnteredBy = enteredBy,
            IsExternallyManaged = isExternallyManaged,
            StartDate = startDate,
        };
    }

    private static BasalSchedule CreateBasalSchedule(Guid? correlationId, string profileName = "Default")
    {
        return new BasalSchedule
        {
            Id = Guid.NewGuid(),
            CorrelationId = correlationId,
            ProfileName = profileName,
            Entries = [new ScheduleEntry { Time = "00:00", Value = 1.0, TimeAsSeconds = 0 }]
        };
    }

    private static CarbRatioSchedule CreateCarbRatioSchedule(Guid? correlationId, string profileName = "Default")
    {
        return new CarbRatioSchedule
        {
            Id = Guid.NewGuid(),
            CorrelationId = correlationId,
            ProfileName = profileName,
            Entries = [new ScheduleEntry { Time = "00:00", Value = 10.0, TimeAsSeconds = 0 }]
        };
    }

    private static SensitivitySchedule CreateSensitivitySchedule(Guid? correlationId, string profileName = "Default")
    {
        return new SensitivitySchedule
        {
            Id = Guid.NewGuid(),
            CorrelationId = correlationId,
            ProfileName = profileName,
            Entries = [new ScheduleEntry { Time = "00:00", Value = 50.0, TimeAsSeconds = 0 }]
        };
    }

    private static TargetRangeSchedule CreateTargetRangeSchedule(Guid? correlationId, string profileName = "Default")
    {
        return new TargetRangeSchedule
        {
            Id = Guid.NewGuid(),
            CorrelationId = correlationId,
            ProfileName = profileName,
            Entries = [new TargetRangeEntry { Time = "00:00", Low = 80, High = 120, TimeAsSeconds = 0 }]
        };
    }

    private void SetupGetLatest(TherapySettings settings)
    {
        _therapyRepo.Setup(r => r.GetAsync(null, null, null, null, 1, 0, true, default))
            .ReturnsAsync([settings]);
    }

    private void SetupCorrelationLookups(
        Guid correlationId,
        BasalSchedule[]? basals = null,
        CarbRatioSchedule[]? carbRatios = null,
        SensitivitySchedule[]? sensitivities = null,
        TargetRangeSchedule[]? targetRanges = null)
    {
        _basalRepo.Setup(r => r.GetByCorrelationIdAsync(correlationId, default))
            .ReturnsAsync(basals ?? Array.Empty<BasalSchedule>());
        _carbRatioRepo.Setup(r => r.GetByCorrelationIdAsync(correlationId, default))
            .ReturnsAsync(carbRatios ?? Array.Empty<CarbRatioSchedule>());
        _sensitivityRepo.Setup(r => r.GetByCorrelationIdAsync(correlationId, default))
            .ReturnsAsync(sensitivities ?? Array.Empty<SensitivitySchedule>());
        _targetRangeRepo.Setup(r => r.GetByCorrelationIdAsync(correlationId, default))
            .ReturnsAsync(targetRanges ?? Array.Empty<TargetRangeSchedule>());
    }

    #endregion
}

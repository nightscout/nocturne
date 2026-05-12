using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Tests.Services.V4;

public class ProfileDecomposerBatchTests : IDisposable
{
    private readonly NocturneDbContext _context;
    private readonly Mock<ITherapySettingsRepository> _therapySettingsRepoMock;
    private readonly Mock<IBasalScheduleRepository> _basalScheduleRepoMock;
    private readonly Mock<ICarbRatioScheduleRepository> _carbRatioScheduleRepoMock;
    private readonly Mock<ISensitivityScheduleRepository> _sensitivityScheduleRepoMock;
    private readonly Mock<ITargetRangeScheduleRepository> _targetRangeScheduleRepoMock;
    private readonly ProfileDecomposer _decomposer;

    public ProfileDecomposerBatchTests()
    {
        _context = TestDbContextFactory.CreateInMemoryContext();
        _context.TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

        _therapySettingsRepoMock = new Mock<ITherapySettingsRepository>();
        _basalScheduleRepoMock = new Mock<IBasalScheduleRepository>();
        _carbRatioScheduleRepoMock = new Mock<ICarbRatioScheduleRepository>();
        _sensitivityScheduleRepoMock = new Mock<ISensitivityScheduleRepository>();
        _targetRangeScheduleRepoMock = new Mock<ITargetRangeScheduleRepository>();

        SetupBulkCreateReturnsInput(_therapySettingsRepoMock);
        SetupBulkCreateReturnsInput(_basalScheduleRepoMock);
        SetupBulkCreateReturnsInput(_carbRatioScheduleRepoMock);
        SetupBulkCreateReturnsInput(_sensitivityScheduleRepoMock);
        SetupBulkCreateReturnsInput(_targetRangeScheduleRepoMock);

        _decomposer = new ProfileDecomposer(
            _context,
            _therapySettingsRepoMock.Object,
            _basalScheduleRepoMock.Object,
            _carbRatioScheduleRepoMock.Object,
            _sensitivityScheduleRepoMock.Object,
            _targetRangeScheduleRepoMock.Object,
            NullLogger<ProfileDecomposer>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task DecomposeBatchAsync_FlattenAndBulkInsertSchedules()
    {
        // Arrange - 2 profiles with 1 store each
        var profiles = new List<Profile>
        {
            CreateProfile("profile1", new Dictionary<string, ProfileData>
            {
                ["Default"] = CreateProfileData()
            }),
            CreateProfile("profile2", new Dictionary<string, ProfileData>
            {
                ["Night"] = CreateProfileData()
            }),
        };

        // Act
        var result = await _decomposer.DecomposeBatchAsync(profiles);

        // Assert - 2 profiles x 5 record types = 10 total records
        _therapySettingsRepoMock.Verify(
            x => x.BulkCreateAsync(
                It.Is<IEnumerable<TherapySettings>>(list => list.Count() == 2),
                It.IsAny<CancellationToken>()),
            Times.Once);

        _basalScheduleRepoMock.Verify(
            x => x.BulkCreateAsync(
                It.Is<IEnumerable<BasalSchedule>>(list => list.Count() == 2),
                It.IsAny<CancellationToken>()),
            Times.Once);

        _carbRatioScheduleRepoMock.Verify(
            x => x.BulkCreateAsync(
                It.Is<IEnumerable<CarbRatioSchedule>>(list => list.Count() == 2),
                It.IsAny<CancellationToken>()),
            Times.Once);

        _sensitivityScheduleRepoMock.Verify(
            x => x.BulkCreateAsync(
                It.Is<IEnumerable<SensitivitySchedule>>(list => list.Count() == 2),
                It.IsAny<CancellationToken>()),
            Times.Once);

        _targetRangeScheduleRepoMock.Verify(
            x => x.BulkCreateAsync(
                It.Is<IEnumerable<TargetRangeSchedule>>(list => list.Count() == 2),
                It.IsAny<CancellationToken>()),
            Times.Once);

        result.CreatedRecords.Should().HaveCount(10);
        result.CorrelationId.Should().NotBeNull();
    }

    [Fact]
    public async Task DecomposeBatchAsync_EmptyBatch_NoRepositoryCalls()
    {
        // Act
        var result = await _decomposer.DecomposeBatchAsync([]);

        // Assert
        _therapySettingsRepoMock.Verify(
            x => x.BulkCreateAsync(It.IsAny<IEnumerable<TherapySettings>>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _basalScheduleRepoMock.Verify(
            x => x.BulkCreateAsync(It.IsAny<IEnumerable<BasalSchedule>>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _carbRatioScheduleRepoMock.Verify(
            x => x.BulkCreateAsync(It.IsAny<IEnumerable<CarbRatioSchedule>>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _sensitivityScheduleRepoMock.Verify(
            x => x.BulkCreateAsync(It.IsAny<IEnumerable<SensitivitySchedule>>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _targetRangeScheduleRepoMock.Verify(
            x => x.BulkCreateAsync(It.IsAny<IEnumerable<TargetRangeSchedule>>(), It.IsAny<CancellationToken>()),
            Times.Never);

        result.CreatedRecords.Should().BeEmpty();
        result.CorrelationId.Should().BeNull();
    }

    [Fact]
    public async Task DecomposeBatchAsync_BulkInsertsTherapySettings()
    {
        // Arrange
        var profiles = new List<Profile>
        {
            CreateProfile("profile1", new Dictionary<string, ProfileData>
            {
                ["Default"] = CreateProfileData(dia: 4.0, timezone: "US/Eastern")
            }),
        };

        // Act
        var result = await _decomposer.DecomposeBatchAsync(profiles);

        // Assert - verify therapy settings were created with correct values
        _therapySettingsRepoMock.Verify(
            x => x.BulkCreateAsync(
                It.Is<IEnumerable<TherapySettings>>(list =>
                    list.Any(ts =>
                        ts.LegacyId == "profile1:Default" &&
                        ts.Dia == 4.0 &&
                        ts.Timezone == "US/Eastern" &&
                        ts.IsDefault == true)),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // A DecompositionBatchEntity was persisted
        var batch = _context.DecompositionBatches.SingleOrDefault(b => b.Id == result.CorrelationId);
        batch.Should().NotBeNull();
        batch!.Source.Should().Be("profile_decomposer_batch");
        batch.TenantId.Should().Be(_context.TenantId);
    }

    [Fact]
    public async Task DecomposeBatchAsync_MultipleStoresPerProfile()
    {
        // Arrange - 1 profile with 2 named stores
        var profiles = new List<Profile>
        {
            CreateProfile("profile1", new Dictionary<string, ProfileData>
            {
                ["Default"] = CreateProfileData(),
                ["Exercise"] = CreateProfileData(dia: 5.0),
            }, defaultProfile: "Default"),
        };

        // Act
        var result = await _decomposer.DecomposeBatchAsync(profiles);

        // Assert - 2 stores x 5 record types = 10 total records
        _therapySettingsRepoMock.Verify(
            x => x.BulkCreateAsync(
                It.Is<IEnumerable<TherapySettings>>(list =>
                    list.Count() == 2 &&
                    list.Any(ts => ts.LegacyId == "profile1:Default" && ts.IsDefault == true) &&
                    list.Any(ts => ts.LegacyId == "profile1:Exercise" && ts.IsDefault == false)),
                It.IsAny<CancellationToken>()),
            Times.Once);

        _basalScheduleRepoMock.Verify(
            x => x.BulkCreateAsync(
                It.Is<IEnumerable<BasalSchedule>>(list => list.Count() == 2),
                It.IsAny<CancellationToken>()),
            Times.Once);

        result.CreatedRecords.Should().HaveCount(10);
    }

    #region Helpers

    private static Profile CreateProfile(
        string id,
        Dictionary<string, ProfileData> store,
        string defaultProfile = "Default")
    {
        return new Profile
        {
            Id = id,
            Mills = 1700000000000,
            DefaultProfile = defaultProfile,
            EnteredBy = "test",
            Store = store,
        };
    }

    private static ProfileData CreateProfileData(
        double dia = 3.0,
        string? timezone = "UTC")
    {
        return new ProfileData
        {
            Dia = dia,
            Timezone = timezone,
            Basal = [new TimeValue { Time = "00:00", Value = 1.0 }],
            CarbRatio = [new TimeValue { Time = "00:00", Value = 10.0 }],
            Sens = [new TimeValue { Time = "00:00", Value = 50.0 }],
            TargetLow = [new TimeValue { Time = "00:00", Value = 80.0 }],
            TargetHigh = [new TimeValue { Time = "00:00", Value = 120.0 }],
        };
    }

    private static void SetupBulkCreateReturnsInput(Mock<ITherapySettingsRepository> mock)
    {
        mock.Setup(x => x.BulkCreateAsync(It.IsAny<IEnumerable<TherapySettings>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<TherapySettings> records, CancellationToken _) => records);
    }

    private static void SetupBulkCreateReturnsInput(Mock<IBasalScheduleRepository> mock)
    {
        mock.Setup(x => x.BulkCreateAsync(It.IsAny<IEnumerable<BasalSchedule>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<BasalSchedule> records, CancellationToken _) => records);
    }

    private static void SetupBulkCreateReturnsInput(Mock<ICarbRatioScheduleRepository> mock)
    {
        mock.Setup(x => x.BulkCreateAsync(It.IsAny<IEnumerable<CarbRatioSchedule>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<CarbRatioSchedule> records, CancellationToken _) => records);
    }

    private static void SetupBulkCreateReturnsInput(Mock<ISensitivityScheduleRepository> mock)
    {
        mock.Setup(x => x.BulkCreateAsync(It.IsAny<IEnumerable<SensitivitySchedule>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<SensitivitySchedule> records, CancellationToken _) => records);
    }

    private static void SetupBulkCreateReturnsInput(Mock<ITargetRangeScheduleRepository> mock)
    {
        mock.Setup(x => x.BulkCreateAsync(It.IsAny<IEnumerable<TargetRangeSchedule>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<TargetRangeSchedule> records, CancellationToken _) => records);
    }

    #endregion
}

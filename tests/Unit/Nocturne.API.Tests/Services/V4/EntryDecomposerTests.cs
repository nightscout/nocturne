using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.V4;

public class EntryDecomposerTests : IDisposable
{
    private readonly NocturneDbContext _context;
    private readonly EntryDecomposer _decomposer;

    public EntryDecomposerTests()
    {
        _context = TestDbContextFactory.CreateInMemoryContext();
        var sgRepo = new SensorGlucoseRepository(_context, NullLogger<SensorGlucoseRepository>.Instance);
        var mgRepo = new MeterGlucoseRepository(_context, NullLogger<MeterGlucoseRepository>.Instance);
        var calRepo = new CalibrationRepository(_context, NullLogger<CalibrationRepository>.Instance);
        _decomposer = new EntryDecomposer(sgRepo, mgRepo, calRepo, NullLogger<EntryDecomposer>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    #region SGV Decomposition

    [Fact]
    public async Task DecomposeAsync_SgvEntry_CreatesSensorGlucoseWithCorrectFields()
    {
        // Arrange
        var entry = new Entry
        {
            Id = "abc123",
            Type = "sgv",
            Mills = 1700000000000,
            Sgv = 120.0,
            Mgdl = 120.0,
            Mmol = 6.7,
            Direction = "Flat",
            Trend = 4,
            TrendRate = 0.5,
            Noise = 1,
            Device = "dexcom-g6",
            App = "xDrip",
            DataSource = "dexcom-connector",
            UtcOffset = -300
        };

        // Act
        var result = await _decomposer.DecomposeAsync(entry);

        // Assert
        result.CorrelationId.Should().NotBeNull();
        result.CreatedRecords.Should().HaveCount(1);
        result.UpdatedRecords.Should().BeEmpty();

        var sg = result.CreatedRecords[0].Should().BeOfType<SensorGlucose>().Subject;
        sg.LegacyId.Should().Be("abc123");
        sg.Mills.Should().Be(1700000000000);
        sg.Mgdl.Should().Be(120.0);
        sg.Mmol.Should().Be(6.7);
        sg.Direction.Should().Be(GlucoseDirection.Flat);
        sg.Trend.Should().Be(GlucoseTrend.Flat);
        sg.TrendRate.Should().Be(0.5);
        sg.Noise.Should().Be(1);
        sg.Device.Should().Be("dexcom-g6");
        sg.App.Should().Be("xDrip");
        sg.DataSource.Should().Be("dexcom-connector");
        sg.UtcOffset.Should().Be(-300);
        sg.CorrelationId.Should().Be(result.CorrelationId);
    }

    [Fact]
    public async Task DecomposeAsync_SgvEntry_UsesSgvOverMgdlWhenBothPresent()
    {
        // Arrange - Sgv and Mgdl may differ; Sgv takes priority for SGV entries
        var entry = new Entry
        {
            Id = "sgv-priority",
            Type = "sgv",
            Mills = 1700000000000,
            Sgv = 130.0,
            Mgdl = 125.0
        };

        // Act
        var result = await _decomposer.DecomposeAsync(entry);

        // Assert
        var sg = result.CreatedRecords[0].Should().BeOfType<SensorGlucose>().Subject;
        sg.Mgdl.Should().Be(130.0, "Sgv should take priority over Mgdl for SGV entries");
    }

    [Fact]
    public async Task DecomposeAsync_SgvEntry_FallsBackToMgdlWhenSgvNull()
    {
        // Arrange
        var entry = new Entry
        {
            Id = "sgv-fallback",
            Type = "sgv",
            Mills = 1700000000000,
            Sgv = null,
            Mgdl = 110.0
        };

        // Act
        var result = await _decomposer.DecomposeAsync(entry);

        // Assert
        var sg = result.CreatedRecords[0].Should().BeOfType<SensorGlucose>().Subject;
        sg.Mgdl.Should().Be(110.0, "should fall back to Mgdl when Sgv is null");
    }

    #endregion

    #region MBG Decomposition

    [Fact]
    public async Task DecomposeAsync_MbgEntry_CreatesMeterGlucoseWithCorrectFields()
    {
        // Arrange
        var entry = new Entry
        {
            Id = "mbg123",
            Type = "mbg",
            Mills = 1700000000000,
            Mbg = 145.0,
            Mgdl = 140.0,
            Mmol = 8.1,
            Device = "contour-next",
            App = "xDrip",
            DataSource = "manual",
            UtcOffset = 60
        };

        // Act
        var result = await _decomposer.DecomposeAsync(entry);

        // Assert
        result.CreatedRecords.Should().HaveCount(1);
        result.UpdatedRecords.Should().BeEmpty();

        var mg = result.CreatedRecords[0].Should().BeOfType<MeterGlucose>().Subject;
        mg.LegacyId.Should().Be("mbg123");
        mg.Mills.Should().Be(1700000000000);
        mg.Mgdl.Should().Be(145.0, "Mbg should take priority over Mgdl for MBG entries");
        mg.Mmol.Should().Be(8.1);
        mg.Device.Should().Be("contour-next");
        mg.App.Should().Be("xDrip");
        mg.DataSource.Should().Be("manual");
        mg.UtcOffset.Should().Be(60);
        mg.CorrelationId.Should().Be(result.CorrelationId);
    }

    [Fact]
    public async Task DecomposeAsync_MbgEntry_FallsBackToMgdlWhenMbgNull()
    {
        // Arrange
        var entry = new Entry
        {
            Id = "mbg-fallback",
            Type = "mbg",
            Mills = 1700000000000,
            Mbg = null,
            Mgdl = 150.0
        };

        // Act
        var result = await _decomposer.DecomposeAsync(entry);

        // Assert
        var mg = result.CreatedRecords[0].Should().BeOfType<MeterGlucose>().Subject;
        mg.Mgdl.Should().Be(150.0, "should fall back to Mgdl when Mbg is null");
    }

    #endregion

    #region CAL Decomposition

    [Fact]
    public async Task DecomposeAsync_CalEntry_CreatesCalibrationWithCorrectFields()
    {
        // Arrange
        var entry = new Entry
        {
            Id = "cal123",
            Type = "cal",
            Mills = 1700000000000,
            Slope = 850.5,
            Intercept = 32100.0,
            Scale = 1.0,
            Device = "dexcom-g6",
            App = "xDrip",
            DataSource = "dexcom-connector",
            UtcOffset = -300
        };

        // Act
        var result = await _decomposer.DecomposeAsync(entry);

        // Assert
        result.CreatedRecords.Should().HaveCount(1);
        result.UpdatedRecords.Should().BeEmpty();

        var cal = result.CreatedRecords[0].Should().BeOfType<Calibration>().Subject;
        cal.LegacyId.Should().Be("cal123");
        cal.Mills.Should().Be(1700000000000);
        cal.Slope.Should().Be(850.5);
        cal.Intercept.Should().Be(32100.0);
        cal.Scale.Should().Be(1.0);
        cal.Device.Should().Be("dexcom-g6");
        cal.App.Should().Be("xDrip");
        cal.DataSource.Should().Be("dexcom-connector");
        cal.UtcOffset.Should().Be(-300);
        cal.CorrelationId.Should().Be(result.CorrelationId);
    }

    #endregion

    #region Idempotency

    [Fact]
    public async Task DecomposeAsync_SameEntryTwice_UpdatesInsteadOfCreatingDuplicate()
    {
        // Arrange
        var entry = new Entry
        {
            Id = "idempotent-test",
            Type = "sgv",
            Mills = 1700000000000,
            Sgv = 120.0,
            Device = "test-device"
        };

        // Act - first call creates
        var firstResult = await _decomposer.DecomposeAsync(entry);
        firstResult.CreatedRecords.Should().HaveCount(1);
        firstResult.UpdatedRecords.Should().BeEmpty();

        // Modify entry data slightly to simulate an update
        entry.Sgv = 125.0;

        // Act - second call updates
        var secondResult = await _decomposer.DecomposeAsync(entry);

        // Assert
        secondResult.CreatedRecords.Should().BeEmpty();
        secondResult.UpdatedRecords.Should().HaveCount(1);

        var updated = secondResult.UpdatedRecords[0].Should().BeOfType<SensorGlucose>().Subject;
        updated.LegacyId.Should().Be("idempotent-test");
        updated.Mgdl.Should().Be(125.0, "should reflect updated Sgv value");
    }

    [Fact]
    public async Task DecomposeAsync_MbgEntryTwice_UpdatesInsteadOfCreatingDuplicate()
    {
        // Arrange
        var entry = new Entry
        {
            Id = "mbg-idempotent",
            Type = "mbg",
            Mills = 1700000000000,
            Mbg = 140.0
        };

        // Act - first call creates
        var firstResult = await _decomposer.DecomposeAsync(entry);
        firstResult.CreatedRecords.Should().HaveCount(1);

        // Act - second call updates
        entry.Mbg = 145.0;
        var secondResult = await _decomposer.DecomposeAsync(entry);

        // Assert
        secondResult.CreatedRecords.Should().BeEmpty();
        secondResult.UpdatedRecords.Should().HaveCount(1);
    }

    [Fact]
    public async Task DecomposeAsync_CalEntryTwice_UpdatesInsteadOfCreatingDuplicate()
    {
        // Arrange
        var entry = new Entry
        {
            Id = "cal-idempotent",
            Type = "cal",
            Mills = 1700000000000,
            Slope = 850.0
        };

        // Act - first call creates
        var firstResult = await _decomposer.DecomposeAsync(entry);
        firstResult.CreatedRecords.Should().HaveCount(1);

        // Act - second call updates
        entry.Slope = 860.0;
        var secondResult = await _decomposer.DecomposeAsync(entry);

        // Assert
        secondResult.CreatedRecords.Should().BeEmpty();
        secondResult.UpdatedRecords.Should().HaveCount(1);
    }

    #endregion

    #region Unknown/Edge Cases

    [Fact]
    public async Task DecomposeAsync_UnknownType_ReturnsEmptyResult()
    {
        // Arrange
        var entry = new Entry
        {
            Id = "unknown-type",
            Type = "foo",
            Mills = 1700000000000
        };

        // Act
        var result = await _decomposer.DecomposeAsync(entry);

        // Assert
        result.CreatedRecords.Should().BeEmpty();
        result.UpdatedRecords.Should().BeEmpty();
        result.CorrelationId.Should().NotBeNull("a correlation ID is always generated");
    }

    [Fact]
    public async Task DecomposeAsync_NullType_ReturnsEmptyResult()
    {
        // Arrange
        var entry = new Entry
        {
            Id = "null-type",
            Type = null!,
            Mills = 1700000000000
        };

        // Act
        var result = await _decomposer.DecomposeAsync(entry);

        // Assert
        result.CreatedRecords.Should().BeEmpty();
        result.UpdatedRecords.Should().BeEmpty();
    }

    [Fact]
    public async Task DecomposeAsync_EmptyType_ReturnsEmptyResult()
    {
        // Arrange
        var entry = new Entry
        {
            Id = "empty-type",
            Type = "",
            Mills = 1700000000000
        };

        // Act
        var result = await _decomposer.DecomposeAsync(entry);

        // Assert
        result.CreatedRecords.Should().BeEmpty();
        result.UpdatedRecords.Should().BeEmpty();
    }

    [Fact]
    public async Task DecomposeAsync_NullId_StillCreatesRecord()
    {
        // Arrange - entry with no ID should still decompose (just can't deduplicate)
        var entry = new Entry
        {
            Id = null,
            Type = "sgv",
            Mills = 1700000000000,
            Sgv = 100.0
        };

        // Act
        var result = await _decomposer.DecomposeAsync(entry);

        // Assert
        result.CreatedRecords.Should().HaveCount(1);
        var sg = result.CreatedRecords[0].Should().BeOfType<SensorGlucose>().Subject;
        sg.LegacyId.Should().BeNull();
        sg.Mgdl.Should().Be(100.0);
    }

    [Fact]
    public async Task DecomposeAsync_SgvWithMissingOptionalFields_HandlesGracefully()
    {
        // Arrange - minimal SGV entry with only required fields
        var entry = new Entry
        {
            Id = "minimal-sgv",
            Type = "sgv",
            Mills = 1700000000000,
            Mgdl = 100.0
        };

        // Act
        var result = await _decomposer.DecomposeAsync(entry);

        // Assert
        result.CreatedRecords.Should().HaveCount(1);
        var sg = result.CreatedRecords[0].Should().BeOfType<SensorGlucose>().Subject;
        sg.Direction.Should().BeNull();
        sg.Trend.Should().BeNull();
        sg.TrendRate.Should().BeNull();
        sg.Noise.Should().BeNull();
        sg.Device.Should().BeNull();
        sg.App.Should().BeNull();
        sg.DataSource.Should().BeNull();
        sg.UtcOffset.Should().BeNull();
    }

    [Fact]
    public async Task DecomposeAsync_TypeCaseInsensitive_HandlesSGVUpperCase()
    {
        // Arrange
        var entry = new Entry
        {
            Id = "uppercase-sgv",
            Type = "SGV",
            Mills = 1700000000000,
            Sgv = 100.0
        };

        // Act
        var result = await _decomposer.DecomposeAsync(entry);

        // Assert
        result.CreatedRecords.Should().HaveCount(1);
        result.CreatedRecords[0].Should().BeOfType<SensorGlucose>();
    }

    #endregion

    #region Static Mapping Methods

    [Theory]
    [InlineData("Flat", GlucoseDirection.Flat)]
    [InlineData("SingleUp", GlucoseDirection.SingleUp)]
    [InlineData("SingleDown", GlucoseDirection.SingleDown)]
    [InlineData("DoubleUp", GlucoseDirection.DoubleUp)]
    [InlineData("DoubleDown", GlucoseDirection.DoubleDown)]
    [InlineData("FortyFiveUp", GlucoseDirection.FortyFiveUp)]
    [InlineData("FortyFiveDown", GlucoseDirection.FortyFiveDown)]
    [InlineData("NOT COMPUTABLE", GlucoseDirection.NotComputable)]
    [InlineData("RATE OUT OF RANGE", GlucoseDirection.RateOutOfRange)]
    [InlineData("NONE", GlucoseDirection.None)]
    public void MapDirection_KnownValues_MapsCorrectly(string input, GlucoseDirection expected)
    {
        EntryDecomposer.MapDirection(input).Should().Be(expected);
    }

    [Fact]
    public void MapDirection_Null_ReturnsNull()
    {
        EntryDecomposer.MapDirection(null).Should().BeNull();
    }

    [Fact]
    public void MapDirection_EmptyString_ReturnsNull()
    {
        EntryDecomposer.MapDirection("").Should().BeNull();
    }

    [Fact]
    public void MapDirection_UnknownValue_ReturnsNull()
    {
        EntryDecomposer.MapDirection("INVALID_DIRECTION").Should().BeNull();
    }

    [Theory]
    [InlineData(0, GlucoseTrend.None)]
    [InlineData(1, GlucoseTrend.DoubleUp)]
    [InlineData(2, GlucoseTrend.SingleUp)]
    [InlineData(3, GlucoseTrend.FortyFiveUp)]
    [InlineData(4, GlucoseTrend.Flat)]
    [InlineData(5, GlucoseTrend.FortyFiveDown)]
    [InlineData(6, GlucoseTrend.SingleDown)]
    [InlineData(7, GlucoseTrend.DoubleDown)]
    [InlineData(8, GlucoseTrend.NotComputable)]
    [InlineData(9, GlucoseTrend.RateOutOfRange)]
    public void MapTrend_KnownValues_MapsCorrectly(int input, GlucoseTrend expected)
    {
        EntryDecomposer.MapTrend(input).Should().Be(expected);
    }

    [Fact]
    public void MapTrend_Null_ReturnsNull()
    {
        EntryDecomposer.MapTrend(null).Should().BeNull();
    }

    [Fact]
    public void MapTrend_OutOfRange_ReturnsNull()
    {
        EntryDecomposer.MapTrend(99).Should().BeNull();
    }

    [Fact]
    public void MapTrend_NegativeValue_ReturnsNull()
    {
        EntryDecomposer.MapTrend(-1).Should().BeNull();
    }

    #endregion
}

using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Mappers;

namespace Nocturne.Infrastructure.Data.Tests.Mappers;

/// <summary>
/// Unit tests for TempBasalToTreatmentMapper
/// </summary>
[Trait("Category", "Unit")]
public class TempBasalToTreatmentMapperTests
{
    #region ToTreatment Tests

    [Fact]
    [Trait("Category", "Unit")]
    public void ToTreatment_MapsBasicFields()
    {
        // Arrange
        var tempBasal = new TempBasal
        {
            Id = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"),
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime,
            EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000 + (30 * 60 * 1000)).UtcDateTime, // 30 minutes later
            Rate = 1.5,
            ScheduledRate = 0.8,
            Origin = TempBasalOrigin.Algorithm,
            App = "openaps",
            Device = "medtronic-pump",
            DataSource = "openaps-connector",
            UtcOffset = -300,
            LegacyId = "legacy-basal-123",
        };

        // Act
        var result = TempBasalToTreatmentMapper.ToTreatment(tempBasal);

        // Assert
        result.Should().NotBeNull();
        result.EventType.Should().Be("Temp Basal");
        result.Mills.Should().Be(1700000000000);
        result.Duration.Should().BeApproximately(30.0, 0.001);
        result.Rate.Should().Be(1.5);
        result.Absolute.Should().Be(1.5);
        result.Temp.Should().Be("absolute");
        result.EnteredBy.Should().Be("openaps");
        result.DataSource.Should().Be("openaps-connector");
        result.UtcOffset.Should().Be(-300);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToTreatment_SuspendedOrigin_RateIsZero()
    {
        // Arrange - suspended origin should always result in rate=0 regardless of Rate value
        var tempBasal = CreateTempBasal(TempBasalOrigin.Suspended, 1.5);

        // Act
        var result = TempBasalToTreatmentMapper.ToTreatment(tempBasal);

        // Assert
        result.Should().NotBeNull();
        result.Rate.Should().Be(0);
        result.Absolute.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToTreatment_AlgorithmOrigin_PreservesRate()
    {
        // Arrange
        var tempBasal = CreateTempBasal(TempBasalOrigin.Algorithm, 1.2);

        // Act
        var result = TempBasalToTreatmentMapper.ToTreatment(tempBasal);

        // Assert
        result.Should().NotBeNull();
        result.EventType.Should().Be("Temp Basal");
        result.Rate.Should().Be(1.2);
        result.Absolute.Should().Be(1.2);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToTreatment_ManualOrigin_PreservesRate()
    {
        // Arrange
        var tempBasal = CreateTempBasal(TempBasalOrigin.Manual, 2.0);

        // Act
        var result = TempBasalToTreatmentMapper.ToTreatment(tempBasal);

        // Assert
        result.Should().NotBeNull();
        result.EventType.Should().Be("Temp Basal");
        result.Rate.Should().Be(2.0);
        result.Absolute.Should().Be(2.0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToTreatment_CarriesOriginInAdditionalProperties()
    {
        // Arrange
        var tempBasal = CreateTempBasal(TempBasalOrigin.Algorithm, 1.0);

        // Act
        var result = TempBasalToTreatmentMapper.ToTreatment(tempBasal);

        // Assert
        result.AdditionalProperties.Should().NotBeNull();
        result.AdditionalProperties!["basalOrigin"].Should().Be("Algorithm");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToTreatment_CarriesScheduledRateInAdditionalProperties()
    {
        // Arrange
        var tempBasal = CreateTempBasal(TempBasalOrigin.Algorithm, 1.5);
        tempBasal.ScheduledRate = 0.8;

        // Act
        var result = TempBasalToTreatmentMapper.ToTreatment(tempBasal);

        // Assert
        result.AdditionalProperties.Should().NotBeNull();
        result.AdditionalProperties!["scheduledRate"].Should().Be(0.8);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToTreatment_NullScheduledRate_NotInAdditionalProperties()
    {
        // Arrange
        var tempBasal = CreateTempBasal(TempBasalOrigin.Algorithm, 1.0);
        tempBasal.ScheduledRate = null;

        // Act
        var result = TempBasalToTreatmentMapper.ToTreatment(tempBasal);

        // Assert
        result.AdditionalProperties.Should().NotBeNull();
        result.AdditionalProperties!.Should().NotContainKey("scheduledRate");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToTreatment_NullEndMills_DurationIsZero()
    {
        // Arrange
        var tempBasal = CreateTempBasal(TempBasalOrigin.Algorithm, 1.0);
        tempBasal.EndTimestamp = null;

        // Act
        var result = TempBasalToTreatmentMapper.ToTreatment(tempBasal);

        // Assert
        result.Duration.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToTreatment_UsesLegacyIdWhenItIsAnObjectId()
    {
        // A caller-supplied 24-hex ObjectId round-trips via an exact LegacyId lookup, so it is
        // emitted verbatim.
        var tempBasal = CreateTempBasal(TempBasalOrigin.Algorithm, 1.0);
        tempBasal.LegacyId = "507f1f77bcf86cd799439011";

        var result = TempBasalToTreatmentMapper.ToTreatment(tempBasal);

        result.Id.Should().Be("507f1f77bcf86cd799439011");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToTreatment_FallsBackToGuidForNonObjectIdLegacyId()
    {
        // A non-ObjectId legacy string (e.g. a synthetic id) would serialize to an unresolvable
        // hash; emit the UUID instead so the wire ObjectId resolves via a uuid prefix range.
        var id = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
        var tempBasal = CreateTempBasal(TempBasalOrigin.Algorithm, 1.0);
        tempBasal.Id = id;
        tempBasal.LegacyId = "legacy-treatment-abc";

        var result = TempBasalToTreatmentMapper.ToTreatment(tempBasal);

        result.Id.Should().Be(id.ToString());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToTreatment_UsesGuidWhenNoLegacyId()
    {
        // Arrange
        var id = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
        var tempBasal = CreateTempBasal(TempBasalOrigin.Algorithm, 1.0);
        tempBasal.Id = id;
        tempBasal.LegacyId = null;

        // Act
        var result = TempBasalToTreatmentMapper.ToTreatment(tempBasal);

        // Assert
        result.Id.Should().Be(id.ToString());
    }

    #endregion

    #region ToTreatments Tests

    [Fact]
    [Trait("Category", "Unit")]
    public void ToTreatments_BatchConversion()
    {
        // Arrange
        var tempBasals = new List<TempBasal>
        {
            CreateTempBasal(TempBasalOrigin.Algorithm, 1.0),
            CreateTempBasal(TempBasalOrigin.Suspended, 0.5),
            CreateTempBasal(TempBasalOrigin.Manual, 2.0),
        };
        tempBasals[0].StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime;
        tempBasals[1].StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700001000000).UtcDateTime;
        tempBasals[2].StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700002000000).UtcDateTime;

        // Act
        var result = TempBasalToTreatmentMapper.ToTreatments(tempBasals).ToList();

        // Assert
        result.Should().HaveCount(3);
        result.Should().AllSatisfy(t => t.EventType.Should().Be("Temp Basal"));

        // Algorithm origin preserves rate
        result[0].Rate.Should().Be(1.0);
        // Suspended origin forces rate to 0
        result[1].Rate.Should().Be(0);
        // Manual origin preserves rate
        result[2].Rate.Should().Be(2.0);
    }

    #endregion

    #region Helper Methods

    private static TempBasal CreateTempBasal(TempBasalOrigin origin, double rate)
    {
        return new TempBasal
        {
            Id = Guid.NewGuid(),
            StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime,
            EndTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700001800000).UtcDateTime, // 30 minutes later
            Rate = rate,
            Origin = origin,
            App = "openaps",
            Device = "medtronic-pump",
            DataSource = "openaps-connector",
            UtcOffset = 0,
        };
    }

    #endregion
}

using Nocturne.Infrastructure.Data.Mappers;

namespace Nocturne.Infrastructure.Data.Tests.Mappers;

/// <summary>
/// Unit tests for TreatmentStateSpanMapper
/// </summary>
[Trait("Category", "Unit")]
public class TreatmentStateSpanMapperTests
{
    #region ToStateSpan Tests

    [Fact]
    [Trait("Category", "Unit")]
    public void ToStateSpan_TempBasalTreatment_MapsCorrectly()
    {
        // Arrange
        var treatment = new Treatment
        {
            Id = "treatment-123",
            EventType = "Temp Basal",
            Mills = 1700000000000,
            Duration = 30.0, // 30 minutes
            Rate = 1.5,
            Absolute = 1.5,
            Percent = 150.0,
            Temp = "absolute",
            EnteredBy = "test-user",
            DataSource = "dexcom-connector"
        };

        // Act
        var result = TreatmentStateSpanMapper.ToStateSpan(treatment);

        // Assert
        result.Should().NotBeNull();
        result!.Category.Should().Be(StateSpanCategory.TempBasal);
        result.State.Should().Be("Active");
        result.StartMills.Should().Be(1700000000000);
        result.EndMills.Should().Be(1700000000000 + (30 * 60 * 1000)); // 30 minutes in ms
        result.Source.Should().Be("dexcom-connector");
        result.OriginalId.Should().Be("treatment-123");

        // Verify metadata
        result.Metadata.Should().NotBeNull();
        result.Metadata!["rate"].Should().Be(1.5);
        result.Metadata["absolute"].Should().Be(1.5);
        result.Metadata["percent"].Should().Be(150.0);
        result.Metadata["temp"].Should().Be("absolute");
        result.Metadata["enteredBy"].Should().Be("test-user");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToStateSpan_TempBasalTreatment_UsesEnteredByWhenNoDataSource()
    {
        // Arrange
        var treatment = new Treatment
        {
            Id = "treatment-456",
            EventType = "Temp Basal",
            Mills = 1700000000000,
            Duration = 60.0,
            Rate = 0.5,
            EnteredBy = "loop-app",
            DataSource = null
        };

        // Act
        var result = TreatmentStateSpanMapper.ToStateSpan(treatment);

        // Assert
        result.Should().NotBeNull();
        result!.Source.Should().Be("loop-app");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToStateSpan_TempBasalTreatment_UsesDefaultSourceWhenNoDataSourceOrEnteredBy()
    {
        // Arrange
        var treatment = new Treatment
        {
            Id = "treatment-789",
            EventType = "Temp Basal",
            Mills = 1700000000000,
            Duration = 45.0,
            Rate = 2.0,
            DataSource = null,
            EnteredBy = null
        };

        // Act
        var result = TreatmentStateSpanMapper.ToStateSpan(treatment);

        // Assert
        result.Should().NotBeNull();
        result!.Source.Should().Be("nightscout");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToStateSpan_TempBasalTreatment_NoEndMillsWhenNoDuration()
    {
        // Arrange
        var treatment = new Treatment
        {
            Id = "treatment-no-duration",
            EventType = "Temp Basal",
            Mills = 1700000000000,
            Duration = null,
            Rate = 1.0
        };

        // Act
        var result = TreatmentStateSpanMapper.ToStateSpan(treatment);

        // Assert
        result.Should().NotBeNull();
        result!.EndMills.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToStateSpan_TempBasalTreatment_NoEndMillsWhenZeroDuration()
    {
        // Arrange
        var treatment = new Treatment
        {
            Id = "treatment-zero-duration",
            EventType = "Temp Basal",
            Mills = 1700000000000,
            Duration = 0,
            Rate = 1.0
        };

        // Act
        var result = TreatmentStateSpanMapper.ToStateSpan(treatment);

        // Assert
        result.Should().NotBeNull();
        result!.EndMills.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToStateSpan_NonTempBasalTreatment_ReturnsNull()
    {
        // Arrange
        var treatment = new Treatment
        {
            Id = "treatment-bolus",
            EventType = "Meal Bolus",
            Mills = 1700000000000,
            Insulin = 5.0,
            Carbs = 60.0
        };

        // Act
        var result = TreatmentStateSpanMapper.ToStateSpan(treatment);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToStateSpan_NullTreatment_ReturnsNull()
    {
        // Act
        var result = TreatmentStateSpanMapper.ToStateSpan(null!);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToStateSpan_TreatmentWithNullEventType_ReturnsNull()
    {
        // Arrange
        var treatment = new Treatment
        {
            Id = "treatment-null-event",
            EventType = null,
            Mills = 1700000000000
        };

        // Act
        var result = TreatmentStateSpanMapper.ToStateSpan(treatment);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToStateSpan_TreatmentWithMinimalMetadata_HasNullMetadata()
    {
        // Arrange
        var treatment = new Treatment
        {
            Id = "treatment-minimal",
            EventType = "Temp Basal",
            Mills = 1700000000000,
            Rate = null,
            Absolute = null,
            Percent = null,
            Temp = null,
            EnteredBy = null
        };

        // Act
        var result = TreatmentStateSpanMapper.ToStateSpan(treatment);

        // Assert
        result.Should().NotBeNull();
        result!.Metadata.Should().BeNull();
    }

    #endregion

    #region ToTreatment Tests

    [Fact]
    [Trait("Category", "Unit")]
    public void ToTreatment_TempBasalStateSpan_MapsCorrectly()
    {
        // Arrange
        var stateSpan = new StateSpan
        {
            Id = "span-123",
            Category = StateSpanCategory.TempBasal,
            State = "Active",
            StartMills = 1700000000000,
            EndMills = 1700000000000 + (30 * 60 * 1000), // 30 minutes later
            Source = "dexcom-connector",
            OriginalId = "original-treatment-id",
            Metadata = new Dictionary<string, object>
            {
                ["rate"] = 1.5,
                ["absolute"] = 1.5,
                ["percent"] = 150.0,
                ["temp"] = "absolute",
                ["enteredBy"] = "test-user"
            }
        };

        // Act
        var result = TreatmentStateSpanMapper.ToTreatment(stateSpan);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be("original-treatment-id");
        result.EventType.Should().Be("Temp Basal");
        result.Mills.Should().Be(1700000000000);
        result.EndMills.Should().Be(1700000000000 + (30 * 60 * 1000));
        result.Duration.Should().BeApproximately(30.0, 0.001);
        result.DataSource.Should().Be("dexcom-connector");
        result.Rate.Should().Be(1.5);
        result.Absolute.Should().Be(1.5);
        result.Percent.Should().Be(150.0);
        result.Temp.Should().Be("absolute");
        result.EnteredBy.Should().Be("test-user");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToTreatment_StateSpanWithoutOriginalId_UsesStateSpanId()
    {
        // Arrange
        var stateSpan = new StateSpan
        {
            Id = "span-456",
            Category = StateSpanCategory.TempBasal,
            State = "Active",
            StartMills = 1700000000000,
            EndMills = 1700001800000, // 30 minutes later
            Source = "loop",
            OriginalId = null,
            Metadata = new Dictionary<string, object>
            {
                ["rate"] = 0.8
            }
        };

        // Act
        var result = TreatmentStateSpanMapper.ToTreatment(stateSpan);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be("span-456");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToTreatment_StateSpanWithNoEndMills_HasNullDuration()
    {
        // Arrange
        var stateSpan = new StateSpan
        {
            Id = "span-no-end",
            Category = StateSpanCategory.TempBasal,
            State = "Active",
            StartMills = 1700000000000,
            EndMills = null,
            Source = "nightscout"
        };

        // Act
        var result = TreatmentStateSpanMapper.ToTreatment(stateSpan);

        // Assert
        result.Should().NotBeNull();
        result!.Duration.Should().BeNull();
        result.EndMills.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToTreatment_StateSpanWithNoMetadata_HasNullProperties()
    {
        // Arrange
        var stateSpan = new StateSpan
        {
            Id = "span-no-meta",
            Category = StateSpanCategory.TempBasal,
            State = "Active",
            StartMills = 1700000000000,
            EndMills = 1700001800000,
            Source = "nightscout",
            Metadata = null
        };

        // Act
        var result = TreatmentStateSpanMapper.ToTreatment(stateSpan);

        // Assert
        result.Should().NotBeNull();
        result!.Rate.Should().BeNull();
        result.Absolute.Should().BeNull();
        result.Percent.Should().BeNull();
        result.Temp.Should().BeNull();
        result.EnteredBy.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToTreatment_NonTempBasalStateSpan_ReturnsNull()
    {
        // Arrange
        var stateSpan = new StateSpan
        {
            Id = "span-pump-mode",
            Category = StateSpanCategory.PumpMode,
            State = "Automatic",
            StartMills = 1700000000000,
            Source = "pump"
        };

        // Act
        var result = TreatmentStateSpanMapper.ToTreatment(stateSpan);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToTreatment_NullStateSpan_ReturnsNull()
    {
        // Act
        var result = TreatmentStateSpanMapper.ToTreatment(null!);

        // Assert
        result.Should().BeNull();
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(StateSpanCategory.PumpMode)]
    [InlineData(StateSpanCategory.PumpConnectivity)]
    [InlineData(StateSpanCategory.Override)]
    [InlineData(StateSpanCategory.Profile)]
    [InlineData(StateSpanCategory.Sleep)]
    [InlineData(StateSpanCategory.Exercise)]
    [InlineData(StateSpanCategory.Illness)]
    [InlineData(StateSpanCategory.Travel)]
    public void ToTreatment_OtherCategories_ReturnsNull(StateSpanCategory category)
    {
        // Arrange
        var stateSpan = new StateSpan
        {
            Id = "span-other",
            Category = category,
            State = "Active",
            StartMills = 1700000000000
        };

        // Act
        var result = TreatmentStateSpanMapper.ToTreatment(stateSpan);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region IsTempBasalTreatment Tests

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("Temp Basal", true)]
    [InlineData("temp basal", true)]
    [InlineData("TEMP BASAL", true)]
    [InlineData("Temp Basal Start", true)]
    [InlineData("temp basal start", true)]
    [InlineData("TEMP BASAL START", true)]
    [InlineData("TempBasal", true)]
    [InlineData("tempbasal", true)]
    [InlineData("TEMPBASAL", true)]
    [InlineData("Meal Bolus", false)]
    [InlineData("Correction Bolus", false)]
    [InlineData("BG Check", false)]
    [InlineData("Site Change", false)]
    [InlineData("Profile Switch", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsTempBasalTreatment_VariousEventTypes_ReturnsCorrectly(string? eventType, bool expected)
    {
        // Arrange
        var treatment = new Treatment
        {
            Id = "test-treatment",
            EventType = eventType,
            Mills = 1700000000000
        };

        // Act
        var result = TreatmentStateSpanMapper.IsTempBasalTreatment(treatment);

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void IsTempBasalTreatment_NullTreatment_ReturnsFalse()
    {
        // Act
        var result = TreatmentStateSpanMapper.IsTempBasalTreatment(null!);

        // Assert
        result.Should().BeFalse();
    }

    #endregion

    #region Round-Trip Tests

    [Fact]
    [Trait("Category", "Unit")]
    public void RoundTrip_TreatmentToStateSpanAndBack_PreservesData()
    {
        // Arrange
        var originalTreatment = new Treatment
        {
            Id = "round-trip-id",
            EventType = "Temp Basal",
            Mills = 1700000000000,
            Duration = 45.0,
            Rate = 1.2,
            Absolute = 1.2,
            Percent = 120.0,
            Temp = "absolute",
            EnteredBy = "carelink",
            DataSource = "medtronic-connector"
        };

        // Act
        var stateSpan = TreatmentStateSpanMapper.ToStateSpan(originalTreatment);
        var roundTripTreatment = TreatmentStateSpanMapper.ToTreatment(stateSpan!);

        // Assert
        roundTripTreatment.Should().NotBeNull();
        roundTripTreatment!.Id.Should().Be(originalTreatment.Id);
        roundTripTreatment.EventType.Should().Be("Temp Basal");
        roundTripTreatment.Mills.Should().Be(originalTreatment.Mills);
        roundTripTreatment.Duration.Should().BeApproximately(originalTreatment.Duration!.Value, 0.001);
        roundTripTreatment.Rate.Should().Be(originalTreatment.Rate);
        roundTripTreatment.Absolute.Should().Be(originalTreatment.Absolute);
        roundTripTreatment.Percent.Should().Be(originalTreatment.Percent);
        roundTripTreatment.Temp.Should().Be(originalTreatment.Temp);
        roundTripTreatment.EnteredBy.Should().Be(originalTreatment.EnteredBy);
        roundTripTreatment.DataSource.Should().Be(originalTreatment.DataSource);
    }

    #endregion

    #region Metadata Parsing Edge Cases

    [Fact]
    [Trait("Category", "Unit")]
    public void ToTreatment_MetadataWithIntegerValues_ParsesCorrectly()
    {
        // Arrange
        var stateSpan = new StateSpan
        {
            Id = "span-int",
            Category = StateSpanCategory.TempBasal,
            State = "Active",
            StartMills = 1700000000000,
            EndMills = 1700001800000,
            Source = "nightscout",
            Metadata = new Dictionary<string, object>
            {
                ["rate"] = 2, // integer
                ["percent"] = 100L // long
            }
        };

        // Act
        var result = TreatmentStateSpanMapper.ToTreatment(stateSpan);

        // Assert
        result.Should().NotBeNull();
        result!.Rate.Should().Be(2.0);
        result.Percent.Should().Be(100.0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToTreatment_MetadataWithStringValues_ParsesCorrectly()
    {
        // Arrange
        var stateSpan = new StateSpan
        {
            Id = "span-string",
            Category = StateSpanCategory.TempBasal,
            State = "Active",
            StartMills = 1700000000000,
            EndMills = 1700001800000,
            Source = "nightscout",
            Metadata = new Dictionary<string, object>
            {
                ["rate"] = "1.5", // string that can be parsed
                ["temp"] = "absolute"
            }
        };

        // Act
        var result = TreatmentStateSpanMapper.ToTreatment(stateSpan);

        // Assert
        result.Should().NotBeNull();
        result!.Rate.Should().Be(1.5);
        result.Temp.Should().Be("absolute");
    }

    #endregion
}

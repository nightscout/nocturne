using Nocturne.Infrastructure.Data.Mappers;

namespace Nocturne.Infrastructure.Data.Tests.Mappers;

/// <summary>
/// Unit tests for BasalDeliveryStateSpanToTreatmentMapper
/// </summary>
[Trait("Category", "Unit")]
public class BasalDeliveryStateSpanToTreatmentMapperTests
{
    #region ToTreatment Tests

    [Fact]
    [Trait("Category", "Unit")]
    public void ToTreatment_BasalDeliveryStateSpan_MapsCorrectly()
    {
        // Arrange
        var stateSpan = new StateSpan
        {
            Id = "span-123",
            Category = StateSpanCategory.BasalDelivery,
            State = "Active",
            StartMills = 1700000000000,
            EndMills = 1700000000000 + (30 * 60 * 1000), // 30 minutes later
            Source = "mylife-connector",
            OriginalId = "original-basal-id",
            Metadata = new Dictionary<string, object>
            {
                ["rate"] = 1.5,
                ["origin"] = "algorithm",
                ["scheduledRate"] = 0.8
            }
        };

        // Act
        var result = BasalDeliveryStateSpanToTreatmentMapper.ToTreatment(stateSpan);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be("original-basal-id");
        result.EventType.Should().Be("Temp Basal"); // All origins map to Temp Basal
        result.Mills.Should().Be(1700000000000);
        result.EndMills.Should().Be(1700000000000 + (30 * 60 * 1000));
        result.Duration.Should().BeApproximately(30.0, 0.001);
        result.Rate.Should().Be(1.5);
        result.Absolute.Should().Be(1.5);
        result.Temp.Should().Be("absolute");
        result.EnteredBy.Should().Be("mylife-connector");
        result.DataSource.Should().Be("mylife-connector");

        // Verify additional properties are set
        result.AdditionalProperties.Should().NotBeNull();
        result.AdditionalProperties!["scheduledRate"].Should().Be(0.8);
        result.AdditionalProperties["basalOrigin"].Should().Be("algorithm");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToTreatment_AlgorithmOrigin_MapsToTempBasal()
    {
        // Arrange
        var stateSpan = CreateBasalDeliveryStateSpan("algorithm", 1.2);

        // Act
        var result = BasalDeliveryStateSpanToTreatmentMapper.ToTreatment(stateSpan);

        // Assert
        result.Should().NotBeNull();
        result!.EventType.Should().Be("Temp Basal");
        result.Rate.Should().Be(1.2);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToTreatment_ScheduledOrigin_MapsToTempBasal()
    {
        // Arrange
        var stateSpan = CreateBasalDeliveryStateSpan("scheduled", 0.8);

        // Act
        var result = BasalDeliveryStateSpanToTreatmentMapper.ToTreatment(stateSpan);

        // Assert
        result.Should().NotBeNull();
        result!.EventType.Should().Be("Temp Basal");
        result.Rate.Should().Be(0.8);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToTreatment_ManualOrigin_MapsToTempBasal()
    {
        // Arrange
        var stateSpan = CreateBasalDeliveryStateSpan("manual", 2.0);

        // Act
        var result = BasalDeliveryStateSpanToTreatmentMapper.ToTreatment(stateSpan);

        // Assert
        result.Should().NotBeNull();
        result!.EventType.Should().Be("Temp Basal");
        result.Rate.Should().Be(2.0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToTreatment_SuspendedOrigin_MapsToTempBasalWithZeroRate()
    {
        // Arrange - suspended origin should always result in rate=0
        var stateSpan = CreateBasalDeliveryStateSpan("suspended", 0.5); // rate in metadata will be overridden

        // Act
        var result = BasalDeliveryStateSpanToTreatmentMapper.ToTreatment(stateSpan);

        // Assert
        result.Should().NotBeNull();
        result!.EventType.Should().Be("Temp Basal");
        result.Rate.Should().Be(0); // Suspended always means rate=0
        result.Absolute.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToTreatment_SuspendedOriginCaseInsensitive_MapsToZeroRate()
    {
        // Arrange
        var stateSpan = new StateSpan
        {
            Id = "span-suspended",
            Category = StateSpanCategory.BasalDelivery,
            State = "Active",
            StartMills = 1700000000000,
            EndMills = 1700001800000,
            Source = "mylife",
            Metadata = new Dictionary<string, object>
            {
                ["rate"] = 1.0,
                ["origin"] = "SUSPENDED" // uppercase
            }
        };

        // Act
        var result = BasalDeliveryStateSpanToTreatmentMapper.ToTreatment(stateSpan);

        // Assert
        result.Should().NotBeNull();
        result!.Rate.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToTreatment_StateSpanWithoutOriginalId_UsesStateSpanId()
    {
        // Arrange
        var stateSpan = new StateSpan
        {
            Id = "span-456",
            Category = StateSpanCategory.BasalDelivery,
            State = "Active",
            StartMills = 1700000000000,
            EndMills = 1700001800000,
            Source = "pump",
            OriginalId = null,
            Metadata = new Dictionary<string, object>
            {
                ["rate"] = 0.8,
                ["origin"] = "algorithm"
            }
        };

        // Act
        var result = BasalDeliveryStateSpanToTreatmentMapper.ToTreatment(stateSpan);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be("span-456");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToTreatment_StateSpanWithNoEndMills_HasZeroDuration()
    {
        // Arrange
        var stateSpan = new StateSpan
        {
            Id = "span-no-end",
            Category = StateSpanCategory.BasalDelivery,
            State = "Active",
            StartMills = 1700000000000,
            EndMills = null,
            Source = "pump",
            Metadata = new Dictionary<string, object>
            {
                ["rate"] = 1.0,
                ["origin"] = "algorithm"
            }
        };

        // Act
        var result = BasalDeliveryStateSpanToTreatmentMapper.ToTreatment(stateSpan);

        // Assert
        result.Should().NotBeNull();
        // Duration defaults to 0 when EndMills is null (Nightscout behavior)
        result!.Duration.Should().Be(0);
        result.EndMills.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToTreatment_StateSpanWithNoMetadata_HasNullRate()
    {
        // Arrange
        var stateSpan = new StateSpan
        {
            Id = "span-no-meta",
            Category = StateSpanCategory.BasalDelivery,
            State = "Active",
            StartMills = 1700000000000,
            EndMills = 1700001800000,
            Source = "pump",
            Metadata = null
        };

        // Act
        var result = BasalDeliveryStateSpanToTreatmentMapper.ToTreatment(stateSpan);

        // Assert
        result.Should().NotBeNull();
        result!.Rate.Should().BeNull();
        result.Absolute.Should().BeNull();
        result.AdditionalProperties.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToTreatment_NonBasalDeliveryStateSpan_ReturnsNull()
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
        var result = BasalDeliveryStateSpanToTreatmentMapper.ToTreatment(stateSpan);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToTreatment_NullStateSpan_ReturnsNull()
    {
        // Act
        var result = BasalDeliveryStateSpanToTreatmentMapper.ToTreatment(null!);

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
        var result = BasalDeliveryStateSpanToTreatmentMapper.ToTreatment(stateSpan);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToTreatment_CreatedAtIsFormattedCorrectly()
    {
        // Arrange
        var stateSpan = new StateSpan
        {
            Id = "span-timestamp",
            Category = StateSpanCategory.BasalDelivery,
            State = "Active",
            StartMills = 1700000000000, // 2023-11-14T22:13:20.000Z
            EndMills = 1700001800000,
            Source = "pump",
            Metadata = new Dictionary<string, object>
            {
                ["rate"] = 1.0
            }
        };

        // Act
        var result = BasalDeliveryStateSpanToTreatmentMapper.ToTreatment(stateSpan);

        // Assert
        result.Should().NotBeNull();
        result!.Created_at.Should().Be("2023-11-14T22:13:20.000Z");
    }

    #endregion

    #region ToTreatments Tests

    [Fact]
    [Trait("Category", "Unit")]
    public void ToTreatments_MixedCategories_OnlyConvertsBasalDelivery()
    {
        // Arrange
        var stateSpans = new List<StateSpan>
        {
            new StateSpan
            {
                Id = "basal-1",
                Category = StateSpanCategory.BasalDelivery,
                State = "Active",
                StartMills = 1700000000000,
                Source = "pump",
                Metadata = new Dictionary<string, object> { ["rate"] = 1.0 }
            },
            new StateSpan
            {
                Id = "override-1",
                Category = StateSpanCategory.Override,
                State = "Active",
                StartMills = 1700001000000,
                Source = "pump"
            },
            new StateSpan
            {
                Id = "basal-2",
                Category = StateSpanCategory.BasalDelivery,
                State = "Active",
                StartMills = 1700002000000,
                Source = "pump",
                Metadata = new Dictionary<string, object> { ["rate"] = 1.5 }
            },
            new StateSpan
            {
                Id = "pump-mode-1",
                Category = StateSpanCategory.PumpMode,
                State = "Automatic",
                StartMills = 1700003000000,
                Source = "pump"
            }
        };

        // Act
        var result = BasalDeliveryStateSpanToTreatmentMapper.ToTreatments(stateSpans).ToList();

        // Assert
        result.Should().HaveCount(2);
        result.Should().AllSatisfy(t => t.EventType.Should().Be("Temp Basal"));
        result.Select(t => t.Id).Should().Contain(new[] { "basal-1", "basal-2" });
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToTreatments_EmptyCollection_ReturnsEmpty()
    {
        // Arrange
        var stateSpans = new List<StateSpan>();

        // Act
        var result = BasalDeliveryStateSpanToTreatmentMapper.ToTreatments(stateSpans).ToList();

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToTreatments_NoBasalDeliverySpans_ReturnsEmpty()
    {
        // Arrange
        var stateSpans = new List<StateSpan>
        {
            new StateSpan
            {
                Id = "override-1",
                Category = StateSpanCategory.Override,
                State = "Active",
                StartMills = 1700000000000,
                Source = "pump"
            },
            new StateSpan
            {
                Id = "pump-mode-1",
                Category = StateSpanCategory.PumpMode,
                State = "Automatic",
                StartMills = 1700001000000,
                Source = "pump"
            }
        };

        // Act
        var result = BasalDeliveryStateSpanToTreatmentMapper.ToTreatments(stateSpans).ToList();

        // Assert
        result.Should().BeEmpty();
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
            Category = StateSpanCategory.BasalDelivery,
            State = "Active",
            StartMills = 1700000000000,
            EndMills = 1700001800000,
            Source = "pump",
            Metadata = new Dictionary<string, object>
            {
                ["rate"] = 2, // integer
                ["utcOffset"] = 60 // integer minutes
            }
        };

        // Act
        var result = BasalDeliveryStateSpanToTreatmentMapper.ToTreatment(stateSpan);

        // Assert
        result.Should().NotBeNull();
        result!.Rate.Should().Be(2.0);
        result.UtcOffset.Should().Be(60);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToTreatment_MetadataWithStringValues_ParsesCorrectly()
    {
        // Arrange
        var stateSpan = new StateSpan
        {
            Id = "span-string",
            Category = StateSpanCategory.BasalDelivery,
            State = "Active",
            StartMills = 1700000000000,
            EndMills = 1700001800000,
            Source = "pump",
            Metadata = new Dictionary<string, object>
            {
                ["rate"] = "1.5", // string that can be parsed
                ["origin"] = "algorithm"
            }
        };

        // Act
        var result = BasalDeliveryStateSpanToTreatmentMapper.ToTreatment(stateSpan);

        // Assert
        result.Should().NotBeNull();
        result!.Rate.Should().Be(1.5);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ToTreatment_MetadataWithJsonElement_ParsesCorrectly()
    {
        // Arrange - simulate JSON deserialization scenario
        var json = """{"rate": 1.25, "origin": "algorithm"}""";
        var doc = JsonDocument.Parse(json);
        var metadata = new Dictionary<string, object>();
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            metadata[prop.Name] = prop.Value;
        }

        var stateSpan = new StateSpan
        {
            Id = "span-json",
            Category = StateSpanCategory.BasalDelivery,
            State = "Active",
            StartMills = 1700000000000,
            EndMills = 1700001800000,
            Source = "pump",
            Metadata = metadata
        };

        // Act
        var result = BasalDeliveryStateSpanToTreatmentMapper.ToTreatment(stateSpan);

        // Assert
        result.Should().NotBeNull();
        result!.Rate.Should().Be(1.25);
        result.AdditionalProperties.Should().NotBeNull();
        result.AdditionalProperties!["basalOrigin"].Should().Be("algorithm");
    }

    #endregion

    #region Helper Methods

    private static StateSpan CreateBasalDeliveryStateSpan(string origin, double rate)
    {
        return new StateSpan
        {
            Id = $"span-{origin}",
            Category = StateSpanCategory.BasalDelivery,
            State = "Active",
            StartMills = 1700000000000,
            EndMills = 1700001800000, // 30 minutes later
            Source = "mylife-connector",
            Metadata = new Dictionary<string, object>
            {
                ["rate"] = rate,
                ["origin"] = origin
            }
        };
    }

    #endregion
}

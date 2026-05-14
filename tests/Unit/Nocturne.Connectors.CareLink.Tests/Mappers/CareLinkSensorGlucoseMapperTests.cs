using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Connectors.CareLink.Mappers;
using Nocturne.Connectors.CareLink.Models;
using Nocturne.Core.Constants;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Connectors.CareLink.Tests.Mappers;

public class CareLinkSensorGlucoseMapperTests
{
    private readonly CareLinkSensorGlucoseMapper _mapper = new(NullLogger.Instance);

    private static CareLinkData CreateTestData(int sgValue = 120, string? bgUnits = null) => new()
    {
        Sgs =
        [
            new CareLinkSensorGlucose { Sg = sgValue, Datetime = "2024-01-15T14:30:00", Kind = "SG" },
            new CareLinkSensorGlucose { Sg = 115, Datetime = "2024-01-15T14:25:00", Kind = "SG" },
        ],
        LastSGTrend = "UP",
        MedicalDeviceTime = "2024-01-15T14:30:00",
        CurrentServerTime = new DateTimeOffset(2024, 1, 15, 13, 30, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(),
        MedicalDeviceFamily = "BLE",
        BgUnits = bgUnits,
    };

    [Fact]
    public void Map_ReturnsSensorGlucoseRecords_FilteringZeroValues()
    {
        var data = CreateTestData();
        data.Sgs!.Add(new CareLinkSensorGlucose { Sg = 0, Datetime = "2024-01-15T14:20:00", Kind = "SG" });

        var result = _mapper.Map(data);

        result.Should().HaveCount(2);
        result.Should().OnlyContain(sg => sg.Mgdl > 0);
    }

    [Fact]
    public void Map_AppliesTrendToMostRecentOnly()
    {
        var data = CreateTestData();
        var result = _mapper.Map(data);

        result[0].Direction.Should().Be(GlucoseDirection.SingleUp);
        result[1].Direction.Should().Be(GlucoseDirection.None);
    }

    [Fact]
    public void Map_SetsMgdlDirectly_WhenNoUnitsSpecified()
    {
        var data = CreateTestData(120);
        var result = _mapper.Map(data);
        result[0].Mgdl.Should().Be(120);
    }

    [Fact]
    public void Map_ConvertsMmolToMgdl()
    {
        var data = CreateTestData(0, "mmol/L");
        data.Sgs = [new CareLinkSensorGlucose { Sg = 7, Datetime = "2024-01-15T14:30:00", Kind = "SG" }];

        var result = _mapper.Map(data);
        result.Should().NotBeEmpty();
        result[0].Mgdl.Should().BeApproximately(7 * 18.0182, 0.01);
    }

    [Fact]
    public void Map_SetsDataSourceAndDevice()
    {
        var data = CreateTestData();
        var result = _mapper.Map(data);

        result[0].DataSource.Should().Be(DataSources.CareLinkConnector);
        result[0].Device.Should().Be("CareLink BLE");
    }

    [Fact]
    public void Map_ReturnsEmpty_WhenSgsNull()
    {
        var data = new CareLinkData { Sgs = null };
        _mapper.Map(data).Should().BeEmpty();
    }

    [Fact]
    public void Map_FiltersNonSgKinds()
    {
        var data = CreateTestData();
        data.Sgs!.Add(new CareLinkSensorGlucose { Sg = 100, Datetime = "2024-01-15T14:20:00", Kind = "Insulin" });

        var result = _mapper.Map(data);
        result.Should().HaveCount(2);
    }
}

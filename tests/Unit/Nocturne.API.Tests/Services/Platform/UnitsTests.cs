using System.Globalization;
using Nocturne.API.Services.Platform;
using Nocturne.Core.Clinical.Statistics;
using Xunit;

namespace Nocturne.API.Tests.Services.Platform;

/// <summary>
/// Tests for glucose unit conversions with 1:1 legacy compatibility
/// Based on legacy units.test.js
/// </summary>
[Parity("units.test.js")]
public class UnitsTests
{
    private readonly StatisticsService _statisticsService = new();

    [Fact]
    public void MgdlToMMOL_ShouldConvert99ToFivePointFive()
    {
        Assert.Equal("5.5", _statisticsService.MgdlToMMOLString(99));
    }

    [Fact]
    public void MgdlToMMOL_ShouldConvert180ToTenPointZero()
    {
        Assert.Equal("10.0", _statisticsService.MgdlToMMOLString(180));
    }

    [Fact]
    public void MmolToMgdl_ShouldConvertFivePointFiveTo99()
    {
        Assert.Equal(99, _statisticsService.MmolToMGDL(5.5));
    }

    [Fact]
    public void MmolToMgdl_ShouldConvertTenPointZeroTo180()
    {
        Assert.Equal(180, _statisticsService.MmolToMGDL(10.0));
    }

    [Fact]
    public void MgdlToMMOL_ToMmolToMgdl_ShouldRoundTrip()
    {
        var mmol = _statisticsService.MgdlToMMOLString(99);
        var result = _statisticsService.MmolToMGDL(
            double.Parse(mmol, CultureInfo.InvariantCulture)
        );

        Assert.Equal(99, result);
    }

    [Fact]
    public void MmolToMgdl_ToMgdlToMMOL_ShouldRoundTrip()
    {
        var mgdl = _statisticsService.MmolToMGDL(5.5);
        var result = _statisticsService.MgdlToMMOLString(mgdl);

        Assert.Equal("5.5", result);
    }
}

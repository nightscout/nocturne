using System.Globalization;
using Nocturne.API.Services.Platform;
using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Platform;

/// <summary>
/// Tests for glucose unit conversions with 1:1 legacy compatibility
/// Based on legacy units.test.js
/// </summary>
[Parity("units.test.js")]
public class UnitsTests
{
    private readonly StatisticsService _statisticsService = new(TimeProvider.System);

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
        var mmol = GlucoseUnits.FromMgdl(99, GlucoseUnit.Mmol).ToString("F1", CultureInfo.InvariantCulture);
        var result = _statisticsService.MmolToMGDL(
            double.Parse(mmol, CultureInfo.InvariantCulture)
        );

        Assert.Equal(99, result);
    }

    [Fact]
    public void MmolToMgdl_ToMgdlToMMOL_ShouldRoundTrip()
    {
        var mgdl = _statisticsService.MmolToMGDL(5.5);
        var result = GlucoseUnits.FromMgdl(mgdl, GlucoseUnit.Mmol).ToString("F1", CultureInfo.InvariantCulture);

        Assert.Equal("5.5", result);
    }
}

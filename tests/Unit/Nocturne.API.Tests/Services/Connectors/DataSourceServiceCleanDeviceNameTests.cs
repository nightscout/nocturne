using System.Globalization;
using FluentAssertions;
using Nocturne.API.Services.Connectors;
using Xunit;

namespace Nocturne.API.Tests.Services.Connectors;

/// <summary>
/// The display name derived for an unrecognised device is part of the API response, so it must not
/// depend on the server's locale — a Turkish-locale host must not spell "xDrip" with a dotless i.
/// </summary>
[Trait("Category", "Unit")]
public class DataSourceServiceCleanDeviceNameTests
{
    [Theory]
    [InlineData("XDRIP-IPHONE", "Xdrip Iphone")]
    [InlineData("my_INSULIN-pump", "My Insulin Pump")]
    [InlineData("  dexcom-connector  ", "Dexcom Connector")]
    public void CleanDeviceName_TitleCasesInvariantly(string deviceId, string expected)
    {
        DataSourceService.CleanDeviceName(deviceId).Should().Be(expected);
    }

    [Theory]
    [InlineData("tr-TR")]
    [InlineData("az-AZ")]
    [InlineData("lt-LT")]
    [InlineData("en-US")]
    public void CleanDeviceName_IgnoresTheServerCulture(string culture)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            DataSourceService.CleanDeviceName("XDRIP-IPHONE").Should().Be("Xdrip Iphone");
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}

using FluentAssertions;
using Nocturne.Core.Constants;
using Xunit;

namespace Nocturne.Core.Constants.Tests;

public class DataSourcesTests
{
    [Fact]
    public void GetDefaultUpdateIntervalMinutes_DexcomConnector_Returns5()
    {
        DataSources.GetDefaultUpdateIntervalMinutes(DataSources.DexcomConnector).Should().Be(5);
    }

    [Fact]
    public void GetDefaultUpdateIntervalMinutes_LibreConnector_Returns1()
    {
        DataSources.GetDefaultUpdateIntervalMinutes(DataSources.LibreConnector).Should().Be(1);
    }

    [Fact]
    public void GetDefaultUpdateIntervalMinutes_UnknownSource_Returns5()
    {
        DataSources.GetDefaultUpdateIntervalMinutes("unknown-source").Should().Be(5);
    }

    [Fact]
    public void GetDefaultUpdateIntervalMinutes_NullSource_Returns5()
    {
        DataSources.GetDefaultUpdateIntervalMinutes(null).Should().Be(5);
    }
}

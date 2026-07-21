using Microsoft.Extensions.Configuration;
using Nocturne.API.Services.Glucose;
using Xunit;

namespace Nocturne.API.Tests.Services.Glucose;

public class PredictionConfigurationTests
{
    [Fact]
    public void ResolveSource_NoConfiguration_DefaultsToDeviceStatus()
    {
        // Predictions must work out of the box: an AID uploader's curves should render without
        // any per-deployment Predictions__Source configuration.
        var configuration = new ConfigurationBuilder().Build();

        Assert.Equal(PredictionSource.DeviceStatus, PredictionOptions.ResolveSource(configuration));
    }

    [Theory]
    [InlineData("None", PredictionSource.None)]
    [InlineData("DeviceStatus", PredictionSource.DeviceStatus)]
    [InlineData("OrefWasm", PredictionSource.OrefWasm)]
    public void ResolveSource_ExplicitConfiguration_Wins(string configured, PredictionSource expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Predictions:Source"] = configured })
            .Build();

        Assert.Equal(expected, PredictionOptions.ResolveSource(configuration));
    }
}

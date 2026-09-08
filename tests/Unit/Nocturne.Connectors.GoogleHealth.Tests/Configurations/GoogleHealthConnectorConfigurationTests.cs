using FluentAssertions;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.GoogleHealth.Configurations;
using Xunit;

namespace Nocturne.Connectors.GoogleHealth.Tests.Configurations;

public class GoogleHealthConnectorConfigurationTests
{
    [Fact]
    public void Registration_describes_supported_health_data()
    {
        var registration = ConnectorRegistrationAttribute.DeclaredOn(typeof(GoogleHealthConnectorConfiguration));

        registration.SupportedDataTypes.Should().BeEquivalentTo(new[]
        {
            SyncDataType.Steps,
            SyncDataType.HeartRate,
            SyncDataType.BodyWeight,
            SyncDataType.Sleep
        });
        registration.SupportsHistoricalSync.Should().BeTrue();
        registration.MaxHistoricalDays.Should().Be(0);
    }

    [Fact]
    public void Per_type_toggles_control_enabled_data_types()
    {
        var configuration = ValidConfiguration();
        configuration.SyncHeartRate = false;

        configuration.IsDataTypeEnabled(SyncDataType.Steps).Should().BeTrue();
        configuration.IsDataTypeEnabled(SyncDataType.HeartRate).Should().BeFalse();
    }

    [Theory]
    [InlineData("http://example.com/settings/connectors/google-health/callback")]
    [InlineData("https://example.com/other")]
    [InlineData("https://example.com/settings/connectors/google-health/callback?query=1")]
    public void Rejects_invalid_callback_urls(string callbackUrl)
    {
        var configuration = ValidConfiguration();
        configuration.CallbackUrl = callbackUrl;

        configuration.Invoking(value => value.Validate()).Should().Throw<ArgumentException>();
    }

    private static GoogleHealthConnectorConfiguration ValidConfiguration() => new()
    {
        ClientId = "client.apps.googleusercontent.com",
        ClientSecret = "secret",
        CallbackUrl = "https://example.com/settings/connectors/google-health/callback"
    };
}

using System.Text.Json;
using FluentAssertions;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
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

    [Theory]
    [InlineData("http://localhost/settings/connectors/google-health/callback", true)]
    [InlineData("http://127.0.0.1/settings/connectors/google-health/callback", false)]
    [InlineData("http://example.com/settings/connectors/google-health/callback", false)]
    public void Allows_http_only_for_the_localhost_development_callback(string callbackUrl, bool valid)
    {
        var configuration = ValidConfiguration();
        configuration.CallbackUrl = callbackUrl;

        if (valid)
            configuration.Invoking(value => value.Validate()).Should().NotThrow();
        else
            configuration.Invoking(value => value.Validate()).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Shared_configuration_binder_loads_health_sync_preferences()
    {
        using var json = JsonDocument.Parse("""
            {
              "lookbackDays": 30,
              "importFrom": "2024-01-01T00:00:00+00:00",
              "previewOnly": true,
              "syncSteps": false,
              "syncHeartRate": true
            }
            """);
        var configuration = ValidConfiguration();

        ConnectorConfigurationBinder.ApplyJsonToConfig(json, configuration);

        configuration.HistoryDays.Should().Be(30);
        configuration.ImportFrom.Should().Be("2024-01-01T00:00:00+00:00");
        configuration.PreviewOnly.Should().BeTrue();
        configuration.SyncSteps.Should().BeFalse();
        configuration.SyncHeartRate.Should().BeTrue();
    }

    [Fact]
    public void Shared_secret_binder_loads_oauth_session_metadata()
    {
        var configuration = ValidConfiguration();

        ConnectorConfigurationBinder.ApplySecretsToConfig(new Dictionary<string, string>
        {
            ["refreshToken"] = "refresh",
            ["grantedScopes"] = "scope-a scope-b"
        }, configuration);

        configuration.RefreshToken.Should().Be("refresh");
        configuration.GrantedScopes.Should().Be("scope-a scope-b");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(91)]
    public void Rejects_invalid_lookback_days(int days)
    {
        var configuration = ValidConfiguration();
        configuration.HistoryDays = days;

        configuration.Invoking(value => value.Validate()).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rejects_invalid_import_start()
    {
        var configuration = ValidConfiguration();
        configuration.ImportFrom = "not-a-date";

        configuration.Invoking(value => value.Validate()).Should().Throw<ArgumentException>();
    }

    private static GoogleHealthConnectorConfiguration ValidConfiguration() => new()
    {
        ClientId = "client.apps.googleusercontent.com",
        ClientSecret = "secret",
        CallbackUrl = "https://example.com/settings/connectors/google-health/callback"
    };
}

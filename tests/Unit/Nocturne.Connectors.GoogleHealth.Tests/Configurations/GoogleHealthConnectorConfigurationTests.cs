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
    [InlineData("https://nocturne.example/api/v4/google-health/oauth/callback", true)]
    [InlineData("http://localhost:1612/api/v4/google-health/oauth/callback", true)]
    [InlineData("http://nocturne.example/api/v4/google-health/oauth/callback", false)]
    [InlineData("http://127.0.0.1/api/v4/google-health/oauth/callback", false)]
    [InlineData("https://nocturne.example/settings/connectors/google-health/callback", false)]
    [InlineData("https://nocturne.example/api/v4/google-health/oauth/callback?x=1", false)]
    [InlineData("https://user@nocturne.example/api/v4/google-health/oauth/callback", false)]
    [InlineData("", false)]
    public void Redirect_uri_must_be_the_api_callback_over_https_or_loopback(string redirectUri, bool valid) =>
        GoogleHealthPlatformOptions.IsValidRedirectUri(redirectUri).Should().Be(valid);

    [Theory]
    [InlineData("client.apps.googleusercontent.com", "secret", true)]
    [InlineData("client.example.com", "secret", false)]
    [InlineData("client.apps.googleusercontent.com", " ", false)]
    [InlineData("", "", false)]
    public void Platform_is_configured_only_with_a_google_client_id_and_secret(
        string clientId, string clientSecret, bool configured)
    {
        var options = new GoogleHealthPlatformOptions
        {
            ClientId = clientId,
            ClientSecret = clientSecret,
            RedirectUri = "https://nocturne.example" + GoogleHealthPlatformOptions.CallbackPath
        };

        options.IsConfigured.Should().Be(configured);
    }

    [Fact]
    public void Platform_is_not_configured_without_a_redirect_uri()
    {
        var options = new GoogleHealthPlatformOptions
        {
            ClientId = "client.apps.googleusercontent.com",
            ClientSecret = "secret"
        };

        options.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void A_tenants_stored_client_is_read_only_as_the_session_stamp()
    {
        using var json = JsonDocument.Parse("""
            {
              "clientId": "tenant.apps.googleusercontent.com",
              "callbackUrl": "https://tenant.example/settings/connectors/google-health/callback"
            }
            """);
        var configuration = ValidConfiguration();

        ConnectorConfigurationBinder.ApplyJsonToConfig(json, configuration);
        ConnectorConfigurationBinder.ApplySecretsToConfig(
            new Dictionary<string, string> { ["clientSecret"] = "tenant-secret" }, configuration);

        configuration.SessionClientId.Should().Be("tenant.apps.googleusercontent.com");
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

    private static GoogleHealthConnectorConfiguration ValidConfiguration() => new();
}

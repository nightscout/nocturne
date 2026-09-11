using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Services;

namespace Nocturne.API.Tests.Services;

/// <summary>
/// The snapshot is published in an issue the reporter cannot retract, so its shape is pinned
/// rather than trusted. These tests fail when a field is added, which is the point: adding one
/// has to be a decision someone made, not a consequence of a DTO growing elsewhere.
/// </summary>
public class SupportDiagnosticsResponseTests
{
    private static readonly string[] ExpectedFields =
    [
        nameof(SupportDiagnosticsResponse.GlucoseUnits),
        nameof(SupportDiagnosticsResponse.TimeFormat),
        nameof(SupportDiagnosticsResponse.PatientTimeZone),
        nameof(SupportDiagnosticsResponse.DataSourcePriority),
        nameof(SupportDiagnosticsResponse.AlertRuleCount),
        nameof(SupportDiagnosticsResponse.Connectors),
    ];

    private static readonly string[] ExpectedConnectorFields =
    [
        nameof(SupportConnectorSummary.Name),
        nameof(SupportConnectorSummary.IsEnabled),
        nameof(SupportConnectorSummary.IsHealthy),
        nameof(SupportConnectorSummary.LastSuccessfulSync),
    ];

    [Fact]
    public void Snapshot_CarriesOnlyTheFieldsItWasReviewedWith()
    {
        typeof(SupportDiagnosticsResponse)
            .GetProperties()
            .Select(p => p.Name)
            .Should()
            .BeEquivalentTo(ExpectedFields);
    }

    [Fact]
    public void ConnectorSummary_CarriesOnlyTheFieldsItWasReviewedWith()
    {
        // Deliberately not a projection of ConnectorStatusDto, which also carries StateMessage —
        // a connector's last error, which can quote a tenant-configured URL or a provider's
        // response body — and HasSecrets.
        typeof(SupportConnectorSummary)
            .GetProperties()
            .Select(p => p.Name)
            .Should()
            .BeEquivalentTo(ExpectedConnectorFields);
    }

    [Fact]
    public void Snapshot_SerialisesNothingThatNamesACredential()
    {
        var snapshot = new SupportDiagnosticsResponse
        {
            GlucoseUnits = "mg/dl",
            TimeFormat = "12",
            PatientTimeZone = "America/Chicago",
            DataSourcePriority = "cgm",
            AlertRuleCount = 7,
            Connectors =
            [
                new SupportConnectorSummary
                {
                    Name = "nightscout",
                    IsEnabled = true,
                    IsHealthy = true,
                    LastSuccessfulSync = DateTime.UnixEpoch,
                },
            ],
        };

        var json = JsonSerializer.Serialize(snapshot);

        foreach (var forbidden in new[]
                 {
                     "secret", "token", "password", "apiSecret", "clientSecret",
                     "accessToken", "refreshToken", "url", "serialNumber", "stateMessage",
                 })
        {
            json.Should().NotContainEquivalentOf(forbidden);
        }
    }

    [Fact]
    public void Snapshot_CarriesNoGlucoseValues()
    {
        // A support issue is public; a reading is patient data and has no diagnostic value here.
        typeof(SupportDiagnosticsResponse)
            .GetProperties()
            .Should()
            .NotContain(p => p.Name.Contains("Glucose", StringComparison.OrdinalIgnoreCase)
                && p.Name != nameof(SupportDiagnosticsResponse.GlucoseUnits));
    }
}

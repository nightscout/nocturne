using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Controllers;
using Nocturne.Core.Clinical.Statistics;
using Nocturne.Connectors.GoogleHealth.Configurations;
using Nocturne.Connectors.GoogleHealth.Services;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;

namespace Nocturne.API.Tests.Controllers;

public class HelpReferenceTests
{
    [Fact]
    public void ReferenceMatchesRuntimeReportBandsAndImportContract()
    {
        var action = new MetadataController().GetHelpReference();
        var facts = (HelpReferenceFacts)((OkObjectResult)action.Result!).Value!;
        facts.ReportBands.Should().BeEquivalentTo(new StatisticsService().HourlyBandThresholds);
        facts.ChartBands.Low.Should().Be(GlucoseConstants.TargetBottomMgdl);
        facts.ChartBands.High.Should().Be(GlucoseConstants.TargetTopMgdl);
        facts.MaximumPlausibleGlucose.Should().Be(GlucoseConstants.MaxPlausibleMgdl);
        var google = new GoogleHealthConnectorConfiguration();
        facts.GoogleHealthCallbackPath.Should().Be(GoogleHealthConnectorConfiguration.CallbackPath);
        facts.GoogleHealthSyncIntervalMinutes.Should().Be(google.SyncIntervalMinutes);
        facts.GoogleHealthDefaultHistoryDays.Should().Be(google.HistoryDays);
        facts.GoogleHealthReadScopes.Should().BeEquivalentTo(GoogleHealthClient.SupportedTypes.Select(GoogleHealthClient.ScopeFor).Distinct());
        facts.GoogleHealthCapabilities.Where(c => c.Supported).Select(c => c.DataType).Should().BeEquivalentTo(GoogleHealthClient.SupportedTypes);
        facts.GoogleHealthCapabilities.Should().NotBeEmpty();
        facts.GoogleHealthMinimumHistoryDays.Should().BeLessThanOrEqualTo(google.HistoryDays);
        facts.GoogleHealthMaximumHistoryDays.Should().BeGreaterThanOrEqualTo(google.HistoryDays);
    }
}

using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Authorization;
using Nocturne.API.Controllers.V4.Analytics;
using Nocturne.Core.Clinical.LoopAnalysis;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.LoopAnalysis;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

public partial class AnalyticsReadScopeGuardTests
{
    private static readonly string[] SettingsEvidenceScopes = [Scope.GlucoseRead, Scope.TreatmentsRead, Scope.TherapyRead];

    /// <summary>Every subset of the four Loop Analysis categories.</summary>
    public static TheoryData<string[]> EveryLoopAnalysisScopeCombination()
    {
        var data = new TheoryData<string[]>();
        for (var mask = 0; mask < 1 << AllLoopAnalysisCategories.Length; mask++)
            data.Add([.. AllLoopAnalysisCategories.Where((_, i) => (mask & (1 << i)) != 0)]);
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryLoopAnalysisScopeCombination))]
    public void SettingsEvidence_IsServedWhole_OrWithheldWhole_ForEveryScopeCombination(string[] scopes)
    {
        var served = SettingsEvidenceScopes.All(scopes.Contains);

        var data = LoopAnalysisReadScopeGuard.Redact(RedactionCoverage.Populated<SettingsEvidenceReport>(), Granted(scopes));

        data.WithheldSeries.Contains(LoopAnalysisSeries.SettingsEvidence).Should().Be(!served);
        if (served)
        {
            data.Primary.Should().BeEquivalentTo(RedactionCoverage.Populated<SettingsEvidenceReport>().Primary,
                "a caller with glucose, treatments and therapy reads every figure");
            data.Comparison.Should().NotBeNull();
            return;
        }
        RedactionCoverage.AssertWithheld(
            data,
            [typeof(SettingsEvidenceReport)],
            [
                .. new[]
                {
                    "HistoryClamped", "TimeZoneUnavailableReason", "PrimaryRange", "ComparisonRange", "AppliedFilters",
                    "IgnoredFilters", "WithheldSeries", "UnavailableFilters", "ProfileNames", "WithheldReason", "Rules",
                    "HistoryWindowHours",
                }.Select(p => $"{nameof(SettingsEvidenceReport)}.{p}"),
            ]);
    }

    [Fact]
    public void SettingsEvidence_GateIsReportsRead() =>
        GateOn<LoopAnalysisController>(nameof(LoopAnalysisController.GetSettingsEvidence))!
            .RequiredScopes.Should().BeEquivalentTo([LoopAnalysisReadScopeGuard.AdmissionScope]);

    [Theory]
    [InlineData(Scope.GlucoseRead)]
    [InlineData(Scope.TreatmentsRead)]
    [InlineData(Scope.TherapyRead)]
    public async Task SettingsEvidence_Handler_WithoutAnyItNeeds_ComputesNothing_AndSaysItIsWithheld(string missing)
    {
        var (controller, service) = LoopAnalysisHandler(
            Granted([Scope.ReportsRead, .. AllLoopAnalysisCategories.Except([missing])]));
        service
            .Setup(s => s.GetProfileNamesAsync(It.IsAny<LoopAnalysisFilter>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ["Default"]);

        var data = (await controller.GetSettingsEvidence(Week(q => q.Compare = LoopAnalysisComparison.Previous)))
            .Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<SettingsEvidenceReport>().Subject;

        data.WithheldSeries.Should().Contain(LoopAnalysisSeries.SettingsEvidence);
        data.WithheldReason.Should().Be(SettingsEvidenceWithheldReason.ReadScopes);
        data.ProfileNames.Should().Equal(
            missing == Scope.TherapyRead ? [] : ["Default"],
            "the profile picker still lists what the caller may see");
        data.Rules.CarbRatioWindowMs.Should().Be(ObservedCarbRatio.WindowAfterMs);
        data.Primary.Should().BeNull("an empty range would read as no evidence rather than none allowed");
        data.Comparison.Should().BeNull();
        data.PrimaryRange.Should().NotBeNull("the envelope still says what was asked for");
        service.Verify(
            s => s.GetSettingsEvidenceAsync(It.IsAny<LoopAnalysisFilter>(), It.IsAny<TimeZoneInfo>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SettingsEvidence_Handler_WithEverythingItNeeds_ServesTheRanges()
    {
        var (controller, service) = LoopAnalysisHandler(Granted([Scope.ReportsRead, .. SettingsEvidenceScopes]));
        service
            .Setup(s => s.GetSettingsEvidenceAsync(It.IsAny<LoopAnalysisFilter>(), It.IsAny<TimeZoneInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new SettingsEvidenceReport
            {
                Primary = new SettingsEvidenceRange { Basal = [new BasalEvidenceBlock { Observed = 1, N = 7 }] },
                ProfileNames = ["Default"],
            });

        var data = (await controller.GetSettingsEvidence(Week()))
            .Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<SettingsEvidenceReport>().Subject;

        data.WithheldSeries.Should().BeEmpty();
        data.Primary!.Basal.Single().Observed.Should().Be(1);
        data.ProfileNames.Should().Equal("Default");
    }
}

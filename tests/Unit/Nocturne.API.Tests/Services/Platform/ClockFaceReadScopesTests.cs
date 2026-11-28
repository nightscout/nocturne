using FluentAssertions;
using Nocturne.API.Services.Platform;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Services.Platform;

/// <summary>
/// Anyone holding a clock link sees what the face serves, so each part takes the scope of what it
/// reads: to be saved onto a face, and to be served from it.
/// </summary>
[Trait("Category", "Unit")]
public class ClockFaceReadScopesTests
{
    [Theory]
    [InlineData("sg")]
    [InlineData("delta")]
    [InlineData("arrow")]
    [InlineData("age")]
    [InlineData("time")]
    [InlineData("text")]
    public void TheFacesBase_TakesNoScope(string type)
    {
        ClockFaceReadScopes.Of(new ClockElement { Type = type }).Should().BeEmpty();
    }

    [Theory]
    [InlineData("summary", Scope.GlucoseRead)]
    [InlineData("iob", Scope.TreatmentsRead)]
    [InlineData("cob", Scope.TreatmentsRead)]
    [InlineData("basal", Scope.TreatmentsRead)]
    [InlineData("tracker", Scope.AlertsRead)]
    [InlineData("trackers", Scope.AlertsRead)]
    public void AnElement_TakesTheScopeOfWhatItReads(string type, string scope)
    {
        ClockFaceReadScopes.Of(new ClockElement { Type = type })
            .Select(r => r.Scope).Should().Equal(scope);
    }

    [Fact]
    public void AForecast_ReadsGlucoseAndTreatments()
    {
        ClockFaceReadScopes.Of(new ClockElement { Type = "forecast" })
            .Select(r => r.Scope).Should().BeEquivalentTo([Scope.GlucoseRead, Scope.TreatmentsRead]);
    }

    [Fact]
    public void AChart_TakesTheScopesOfTheTracksItDraws()
    {
        var glucoseOnly = new ClockChartConfig
        {
            ShowBolus = false, ShowCarbs = false, ShowIob = false, ShowCob = false, ShowBasal = false,
            ShowPredictions = false, ShowDeviceEvents = false, ShowTrackers = false,
        };

        ClockFaceReadScopes.OfChart(glucoseOnly).Select(r => r.Scope).Should().Equal(Scope.GlucoseRead);
        ClockFaceReadScopes.OfChart(new ClockChartConfig
        {
            ShowBolus = false, ShowCarbs = false, ShowPredictions = true,
            ShowDeviceEvents = true, ShowTrackers = true,
        }).Select(r => r.Scope).Should().BeEquivalentTo(
            [Scope.GlucoseRead, Scope.TreatmentsRead, Scope.DevicesRead, Scope.AlertsRead]);
    }

    [Fact]
    public void AReaderMissingAScope_IsNotServedThePart()
    {
        HashSet<string> glucoseOnly = [Scope.GlucoseRead];

        ClockFaceReadScopes.Allows(glucoseOnly, new ClockElement { Type = "summary" }).Should().BeTrue();
        ClockFaceReadScopes.Allows(glucoseOnly, new ClockElement { Type = "iob" }).Should().BeFalse();
        ClockFaceReadScopes.Allows(glucoseOnly, new ClockElement { Type = "forecast" }).Should().BeFalse();
    }

    [Fact]
    public void AddingAPart_TakesItsScope()
    {
        var after = Face(new ClockElement { Type = "iob" });

        ClockFaceReadScopes.MissingFor(after, before: null, held: new HashSet<string> { Scope.GlucoseRead })
            .Should().Be(Scope.TreatmentsRead);
        ClockFaceReadScopes.MissingFor(after, before: null, held: new HashSet<string> { Scope.GlucoseRead, Scope.TreatmentsRead })
            .Should().BeNull();
    }

    [Fact]
    public void EveryFace_TakesGlucoseForItsLatestReading()
    {
        ClockFaceReadScopes.MissingFor(new ClockFaceConfig(), before: null, held: new HashSet<string>())
            .Should().Be(Scope.GlucoseRead);
        ClockFaceReadScopes.MissingFor(new ClockFaceConfig(), before: new ClockFaceConfig(), held: new HashSet<string>())
            .Should().BeNull();
    }

    [Fact]
    public void KeepingAPartTheFaceAlreadyShowed_TakesNoScope()
    {
        var before = Face(new ClockElement { Type = "cob" });
        var after = Face(new ClockElement { Type = "cob" }, new ClockElement { Type = "sg" });

        ClockFaceReadScopes.MissingFor(after, before, new HashSet<string>()).Should().BeNull();
    }

    [Fact]
    public void TurningOnMarkSeen_TakesTheAlertsScope_EvenWithAlertsAlreadyShown()
    {
        var before = new ClockFaceConfig { Settings = new ClockSettings { ShowAlerts = true } };
        var after = new ClockFaceConfig { Settings = new ClockSettings { ShowAlerts = true, AllowMarkSeen = true } };

        ClockFaceReadScopes.MissingFor(after, before, new HashSet<string> { Scope.GlucoseRead })
            .Should().Be(Scope.AlertsRead);
    }

    private static ClockFaceConfig Face(params ClockElement[] elements) => new()
    {
        Rows = [new ClockRow { Elements = [.. elements] }],
    };
}

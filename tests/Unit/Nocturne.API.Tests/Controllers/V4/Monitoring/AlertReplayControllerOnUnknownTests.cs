using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4.Monitoring;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Monitoring;

[Trait("Category", "Unit")]
public class AlertReplayControllerOnUnknownTests
{
    private static ReplayRuleDefinition Rule(string? onUnknown, int? glucoseFreshnessMinutes = null) => new(
        Guid.NewGuid(), "Low", AlertConditionType.Threshold, """{"schema_version":2,"operator":"<","value":70}""",
        AlertRuleSeverity.Warning, AllowThroughDnd: false, AutoResolveEnabled: false, AutoResolveParams: null,
        OnUnknown: onUnknown, GlucoseFreshnessMinutes: glucoseFreshnessMinutes);

    private static (AlertReplayController Controller, Mock<IAlertReplayService> Service) Create()
    {
        var service = new Mock<IAlertReplayService>();
        var controller = new AlertReplayController(service.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        return (controller, service);
    }

    [Theory]
    [InlineData("Hold")]
    [InlineData("ignore")]
    public async Task A_dry_run_or_firing_search_naming_no_on_unknown_word_is_a_bad_request(string word)
    {
        var (controller, service) = Create();

        var dryRun = await controller.ReplayDryRun(
            new AlertReplayDryRunRequest(null, null, Rule(word)), CancellationToken.None);
        var firings = await controller.FindFirings(new AlertFiringSearchRequest(Rule(word)), CancellationToken.None);

        dryRun.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
        firings.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
        service.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1441)]
    public async Task A_dry_run_or_firing_search_with_a_bound_outside_one_minute_to_a_day_is_a_bad_request(int minutes)
    {
        var (controller, service) = Create();

        var dryRun = await controller.ReplayDryRun(
            new AlertReplayDryRunRequest(null, null, Rule(null, minutes)), CancellationToken.None);
        var firings = await controller.FindFirings(
            new AlertFiringSearchRequest(Rule(null, minutes)), CancellationToken.None);

        dryRun.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
        firings.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(400);
        service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_dry_run_passes_the_bound_to_the_replay()
    {
        var (controller, service) = Create();
        service.Setup(s => s.ReplayDryRunAsync(
                It.IsAny<DateOnly?>(), It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<ReplayRuleOverride>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AlertReplayResult(DateTime.UnixEpoch, DateTime.UnixEpoch, []));

        await controller.ReplayDryRun(
            new AlertReplayDryRunRequest(null, null, Rule(null, 30)), CancellationToken.None);

        service.Verify(s => s.ReplayDryRunAsync(
            null, null, null, null,
            It.Is<ReplayRuleOverride>(o => o.GlucoseFreshnessMinutes == 30),
            It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task A_dry_run_passes_the_word_to_the_replay()
    {
        var (controller, service) = Create();
        service.Setup(s => s.ReplayDryRunAsync(
                It.IsAny<DateOnly?>(), It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
                It.IsAny<ReplayRuleOverride>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AlertReplayResult(DateTime.UnixEpoch, DateTime.UnixEpoch, []));

        await controller.ReplayDryRun(
            new AlertReplayDryRunRequest(null, null, Rule(AlertRuleOnUnknown.NotMet)), CancellationToken.None);

        service.Verify(s => s.ReplayDryRunAsync(
            null, null, null, null,
            It.Is<ReplayRuleOverride>(o => o.OnUnknown == AlertRuleOnUnknown.NotMet),
            It.IsAny<CancellationToken>()));
    }
}

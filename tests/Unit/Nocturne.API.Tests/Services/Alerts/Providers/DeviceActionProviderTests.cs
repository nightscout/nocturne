using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Alerts.Providers;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.ClientDevices;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Providers;

[Trait("Category", "Unit")]
public class DeviceActionProviderTests
{
    private static AlertPayload Payload(Guid? tenantId = null) => new()
    {
        AlertType = AlertConditionType.Threshold,
        RuleName = "Urgent Low",
        GlucoseValue = 54,
        Trend = "SingleDown",
        TrendRate = -2,
        ReadingTimestamp = DateTime.UtcNow,
        ExcursionId = Guid.NewGuid(),
        InstanceId = Guid.NewGuid(),
        TenantId = tenantId ?? Guid.NewGuid(),
        SubjectName = "Rhys",
        ActiveExcursionCount = 1,
        Severity = AlertRuleSeverity.Critical,
    };

    private static DeviceActionProvider CreateProvider(Mock<ISignalRBroadcastService> broadcast)
        => new(broadcast.Object, new MemoryCache(new MemoryCacheOptions()), NullLogger<DeviceActionProvider>.Instance);

    [Fact]
    public async Task SendAsync_suppresses_local_engine_kind_without_broadcasting()
    {
        var broadcast = new Mock<ISignalRBroadcastService>();
        var provider = CreateProvider(broadcast);

        var handled = await provider.SendAsync(
            DeviceKinds.Prelude, Payload(), "{\"capabilities\":[\"torch\"]}", default);

        handled.Should().BeTrue("local-engine suppression is a correct outcome, not a failure");
        broadcast.Verify(b => b.BroadcastDeviceActionAsync(It.IsAny<DeviceActionIntent>()), Times.Never);
    }

    [Fact]
    public async Task SendAsync_broadcasts_intent_for_push_mode_kind()
    {
        var broadcast = new Mock<ISignalRBroadcastService>();
        DeviceActionIntent? captured = null;
        broadcast
            .Setup(b => b.BroadcastDeviceActionAsync(It.IsAny<DeviceActionIntent>()))
            .Callback<DeviceActionIntent>(i => captured = i)
            .Returns(Task.CompletedTask);
        var provider = CreateProvider(broadcast);
        var payload = Payload();

        var handled = await provider.SendAsync(
            DeviceKinds.Companion, payload, "{\"capabilities\":[\"notify\",\"tray_flash\"]}", default);

        handled.Should().BeTrue();
        captured.Should().NotBeNull();
        captured!.Intent.Should().Be("opened");
        captured.TargetKind.Should().Be(DeviceKinds.Companion);
        captured.ExcursionId.Should().Be(payload.ExcursionId);
        captured.RuleName.Should().Be("Urgent Low");
        captured.Severity.Should().Be(AlertRuleSeverity.Critical);
        captured.Capabilities.Should().BeEquivalentTo(["notify", "tray_flash"]);
    }

    [Fact]
    public async Task SendAsync_returns_false_for_unknown_kind()
    {
        var broadcast = new Mock<ISignalRBroadcastService>();
        var provider = CreateProvider(broadcast);

        var handled = await provider.SendAsync("smartfridge", Payload(), null, default);

        handled.Should().BeFalse();
        broadcast.Verify(b => b.BroadcastDeviceActionAsync(It.IsAny<DeviceActionIntent>()), Times.Never);
    }

    [Fact]
    public async Task SendAsync_rate_limits_nudges_per_tenant_kind()
    {
        var broadcast = new Mock<ISignalRBroadcastService>();
        broadcast
            .Setup(b => b.BroadcastDeviceActionAsync(It.IsAny<DeviceActionIntent>()))
            .Returns(Task.CompletedTask);
        var provider = CreateProvider(broadcast);
        var tenant = Guid.NewGuid();

        // Fire well past the window budget for the same tenant+kind.
        for (var i = 0; i < DeviceActionProvider.MaxNudgesPerWindow + 5; i++)
        {
            var handled = await provider.SendAsync(
                DeviceKinds.Companion, Payload(tenant), "{\"capabilities\":[\"notify\"]}", default);
            handled.Should().BeTrue("suppressing over-budget nudges is still 'handled' — the snapshot delivers them");
        }

        // Only the window budget is broadcast live; the rest are suppressed (snapshot still delivers).
        broadcast.Verify(
            b => b.BroadcastDeviceActionAsync(It.IsAny<DeviceActionIntent>()),
            Times.Exactly(DeviceActionProvider.MaxNudgesPerWindow));
    }
}

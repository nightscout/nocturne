using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.ClientDevices;
using Nocturne.Infrastructure.Data;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// Covers the test-fire excursion window for <c>device_action</c> channels: push-mode devices
/// actuate from the active-intents snapshot (which admits excursions with a future EndedAt), so a
/// test fire with a device_action channel must get a short future end instead of ending
/// immediately — otherwise it reports Delivered but can never actuate.
/// </summary>
[Trait("Category", "Unit")]
public class AlertDeliveryServiceTestFireTests
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private sealed class SharedInMemoryFactory(string dbName) : IDbContextFactory<NocturneDbContext>
    {
        public NocturneDbContext CreateDbContext() =>
            TestDbContextFactory.CreateInMemoryContext(dbName);
    }

    private static (AlertDeliveryService service, IDbContextFactory<NocturneDbContext> factory) CreateService()
    {
        var factory = new SharedInMemoryFactory($"test_fire_{Guid.NewGuid()}");
        var tenantAccessor = Mock.Of<ITenantAccessor>(a => a.TenantId == Tenant);
        // Empty provider: every channel provider resolves to null, so dispatch is a no-op.
        var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var service = new AlertDeliveryService(
            factory,
            tenantAccessor,
            Mock.Of<ISignalRBroadcastService>(),
            serviceProvider,
            NullLogger<AlertDeliveryService>.Instance);
        return (service, factory);
    }

    private static AlertPayload Payload() => new()
    {
        AlertType = AlertConditionType.Threshold,
        RuleName = "[Test] Low",
        Severity = AlertRuleSeverity.Warning,
        TenantId = Tenant,
        GlucoseValue = null,
        Trend = null,
        TrendRate = null,
        ReadingTimestamp = DateTime.UtcNow,
        SubjectName = "Test fire",
        ExcursionId = Guid.Empty,
        InstanceId = Guid.Empty,
        ActiveExcursionCount = 0,
    };

    private static AlertRuleChannelSnapshot Channel(ChannelType type, string destination) =>
        new(Guid.NewGuid(), Guid.NewGuid(), type, destination, null, 0);

    [Fact]
    public async Task TestFire_with_device_action_channel_leaves_excursion_open_into_the_future()
    {
        var (service, factory) = CreateService();
        var before = DateTime.UtcNow;

        await service.TestFireAsync(
            Guid.NewGuid(),
            [Channel(ChannelType.DeviceAction, DeviceKinds.Companion)],
            Payload(),
            CancellationToken.None);

        await using var db = await factory.CreateDbContextAsync();
        var excursion = db.AlertExcursions.IgnoreQueryFilters().Single();
        excursion.EndedAt.Should().NotBeNull();
        excursion.EndedAt.Should().BeAfter(before, "the excursion must stay visible to the "
            + "active-intents snapshot for the test window");
        excursion.EndedAt.Should().Be(
            excursion.StartedAt + AlertDeliveryService.DeviceActionTestFireWindow);
    }

    [Fact]
    public async Task TestFire_without_device_action_channel_ends_excursion_immediately()
    {
        var (service, factory) = CreateService();

        await service.TestFireAsync(
            Guid.NewGuid(),
            [Channel(ChannelType.InApp, string.Empty)],
            Payload(),
            CancellationToken.None);

        await using var db = await factory.CreateDbContextAsync();
        var excursion = db.AlertExcursions.IgnoreQueryFilters().Single();
        excursion.EndedAt.Should().Be(excursion.StartedAt);
    }
}

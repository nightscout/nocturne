using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Alerts;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// An <c>/alarm</c> ack names an engine alarm by its excursion and snoozes it through the engine; an
/// ack the engine refuses must leave the alarm sounding on every subscriber.
/// </summary>
[Trait("Category", "Unit")]
public class AlarmAckHandlerTests
{
    private readonly AlarmTestTenant _tenant = new();
    private readonly Mock<INotificationV1Service> _legacy = new();

    private AlarmAckHandler CreateHandler()
    {
        var relay = _tenant.Relay();
        var snooze = new AlertSnoozeService(
            _tenant.Contexts.AsNocturneFactory(),
            _tenant.Accessor,
            Mock.Of<IAlertRepository>(),
            Mock.Of<IAlertRuleRepository>(),
            Mock.Of<IAlertDeliveryService>(),
            Mock.Of<ISensorContextEnricher>(),
            _tenant.Canonical.Object,
            _tenant.Broadcast.Object,
            relay,
            _tenant.Time,
            NullLogger<AlertSnoozeService>.Instance);

        return new AlarmAckHandler(
            _tenant.Contexts.AsNocturneFactory(),
            snooze,
            _legacy.Object,
            _tenant.Time,
            NullLogger<AlarmAckHandler>.Instance);
    }

    [Fact]
    public async Task An_ack_snoozes_the_excursion_for_the_silence_time_and_clears_the_last_alarm()
    {
        var urgent = await _tenant.SeedAsync();

        var result = await CreateHandler().AckAsync(2, urgent.ExcursionId.ToString(), 15 * 60_000 + 1, CancellationToken.None);

        result.Success.Should().BeTrue();
        (await _tenant.InstanceAsync(urgent.InstanceId)).SnoozedUntil.Should().Be(AlarmTestTenant.Now.AddMinutes(16));
        _tenant.Clears.Should().ContainSingle();
        _legacy.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task An_ack_leaves_another_sounding_alarm_uncleared()
    {
        var urgent = await _tenant.SeedAsync();
        await _tenant.SeedAsync(AlertRuleSeverity.Warning, "High");

        var result = await CreateHandler().AckAsync(2, urgent.ExcursionId.ToString(), 60_000, CancellationToken.None);

        result.Success.Should().BeTrue();
        _tenant.Clears.Should().BeEmpty();
    }

    [Fact]
    public async Task An_ack_past_the_snooze_limit_fails_and_clears_nothing()
    {
        var urgent = await _tenant.SeedAsync(snoozeCount: 3);

        var result = await CreateHandler().AckAsync(2, urgent.ExcursionId.ToString(), 60_000, CancellationToken.None);

        result.Success.Should().BeFalse();
        (await _tenant.InstanceAsync(urgent.InstanceId)).SnoozedUntil.Should().BeNull();
        _tenant.Clears.Should().BeEmpty();
    }

    [Fact]
    public async Task An_ack_for_an_excursion_of_another_tenant_finds_nothing()
    {
        var foreign = await _tenant.SeedAsync(tenantId: Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"));

        var result = await CreateHandler().AckAsync(2, foreign.ExcursionId.ToString(), 60_000, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain(nameof(SnoozeResult.NotFound));
        (await _tenant.InstanceAsync(foreign.InstanceId)).SnoozedUntil.Should().BeNull();
    }

    [Fact]
    public async Task A_group_that_names_no_excursion_reaches_only_the_legacy_snooze()
    {
        var urgent = await _tenant.SeedAsync();
        _legacy
            .Setup(l => l.AckNotificationAsync(It.IsAny<NotificationAckRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationAckResponse { Success = true });

        var result = await CreateHandler().AckAsync(2, "default", 60_000, CancellationToken.None);

        result.Success.Should().BeTrue();
        _legacy.Verify(l => l.AckNotificationAsync(
            It.Is<NotificationAckRequest>(r => r.Level == 2 && r.Group == "default" && r.Time == 60_000 && r.SendClear == true),
            It.IsAny<CancellationToken>()), Times.Once);
        (await _tenant.InstanceAsync(urgent.InstanceId)).SnoozedUntil.Should().BeNull();
        _tenant.Sent.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1, 1)]
    [InlineData(60_000, 1)]
    [InlineData(60_001, 2)]
    [InlineData(int.MaxValue, 24 * 60)]
    public void Silence_time_becomes_whole_minutes_between_one_and_a_day(int silenceTimeMs, int minutes)
    {
        AlarmAckHandler.SilenceMinutes(silenceTimeMs).Should().Be(minutes);
    }
}

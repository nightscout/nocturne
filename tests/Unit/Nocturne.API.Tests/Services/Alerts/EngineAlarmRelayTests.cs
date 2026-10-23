using FluentAssertions;
using Nocturne.API.Services.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// The engine on the Nightscout <c>/alarm</c> namespace. Every <c>clear_alarm</c> consumer dismisses
/// every alarm it holds, so an all-clear sent while another alarm still sounds silences it.
/// </summary>
[Trait("Category", "Unit")]
public class EngineAlarmRelayTests
{
    private static readonly DateTimeOffset At = new(AlarmTestTenant.Now);
    private readonly AlarmTestTenant _tenant = new();

    private static AlertPayload Payload(AlertRuleSeverity severity, decimal? mgdl = 54m) => new()
    {
        AlertType = AlertConditionType.SignalLoss,
        RuleName = "Urgent low",
        Severity = severity,
        GlucoseValue = mgdl,
        Trend = null,
        TrendRate = null,
        ReadingTimestamp = AlarmTestTenant.Now,
        ExcursionId = Guid.Parse("0192a3b4-0000-7000-8000-000000000001"),
        InstanceId = Guid.NewGuid(),
        TenantId = AlarmTestTenant.Tenant,
        SubjectName = "Sleepy",
        ActiveExcursionCount = 1,
    };

    [Fact]
    public void A_critical_rule_raises_an_urgent_alarm_named_by_its_excursion()
    {
        var alarm = EngineAlarmRelay.Map(Payload(AlertRuleSeverity.Critical), useMmol: false, At);

        alarm!.EventName.Should().Be(EngineAlarmEvents.UrgentAlarm);
        alarm.Notify.Level.Should().Be(2);
        alarm.Notify.Group.Should().Be("0192a3b4-0000-7000-8000-000000000001");
        alarm.Notify.Plugin.Should().Be("signal_loss");
        alarm.Notify.EventName.Should().Be("signal_loss");
        alarm.Notify.Title.Should().Be("Urgent low");
        alarm.Notify.Message.Should().Be("BG Now: 54 mg/dL");
        alarm.Notify.Timestamp.Should().Be(At.ToUnixTimeMilliseconds());
        alarm.Notify.Clear.Should().BeFalse();
    }

    [Fact]
    public void A_warning_rule_raises_an_alarm()
    {
        var alarm = EngineAlarmRelay.Map(Payload(AlertRuleSeverity.Warning), useMmol: false, At);

        alarm!.EventName.Should().Be(EngineAlarmEvents.Alarm);
        alarm.Notify.Level.Should().Be(1);
    }

    [Fact]
    public void An_info_rule_raises_nothing()
    {
        EngineAlarmRelay.Map(Payload(AlertRuleSeverity.Info), useMmol: false, At).Should().BeNull();
    }

    [Fact]
    public void Glucose_reads_in_the_tenants_units()
    {
        EngineAlarmRelay.Map(Payload(AlertRuleSeverity.Critical, 54m), useMmol: true, At)!
            .Notify.Message.Should().Be("BG Now: 3.0 mmol/L");
    }

    [Fact]
    public void An_alarm_without_a_reading_says_what_fired()
    {
        EngineAlarmRelay.Map(Payload(AlertRuleSeverity.Critical, mgdl: null), useMmol: false, At)!
            .Notify.Message.Should().Be("Urgent low");
    }

    [Fact]
    public async Task Raising_reads_glucose_in_the_tenants_units()
    {
        _tenant.Units("mmol");

        await _tenant.Relay().RaiseAsync(Payload(AlertRuleSeverity.Warning, 72m), CancellationToken.None);

        _tenant.Sent.Should().ContainSingle().Which.Notify.Message.Should().Be("BG Now: 4.0 mmol/L");
    }

    [Fact]
    public async Task Raising_an_info_rule_sends_nothing()
    {
        await _tenant.Relay().RaiseAsync(Payload(AlertRuleSeverity.Info), CancellationToken.None);

        _tenant.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task The_all_clear_waits_while_another_alarm_still_sounds()
    {
        var closing = await _tenant.SeedAsync(AlertRuleSeverity.Warning, "High");
        await _tenant.SeedAsync(AlertRuleSeverity.Critical);
        await _tenant.ResolveAsync(closing.ExcursionId);

        await _tenant.Relay().ClearIfQuietAsync([closing.ExcursionId], CancellationToken.None);

        _tenant.Clears.Should().BeEmpty();
    }

    [Fact]
    public async Task The_all_clear_goes_out_when_the_last_alarm_resolves()
    {
        var first = await _tenant.SeedAsync(AlertRuleSeverity.Warning, "High");
        var last = await _tenant.SeedAsync(AlertRuleSeverity.Critical);
        var relay = _tenant.Relay();

        await _tenant.ResolveAsync(first.ExcursionId);
        await relay.ClearIfQuietAsync([first.ExcursionId], CancellationToken.None);
        await _tenant.ResolveAsync(last.ExcursionId);
        await relay.ClearIfQuietAsync([last.ExcursionId], CancellationToken.None);

        _tenant.Clears.Should().ContainSingle().Which.Notify.Clear.Should().BeTrue();
    }

    [Fact]
    public async Task A_members_mute_does_not_quiet_the_tenants_alarm()
    {
        var closing = await _tenant.SeedAsync(AlertRuleSeverity.Warning, "High");
        var muted = await _tenant.SeedAsync(AlertRuleSeverity.Critical);
        await _tenant.MuteAsync(muted.ExcursionId);
        await _tenant.ResolveAsync(closing.ExcursionId);

        await _tenant.Relay().ClearIfQuietAsync([closing.ExcursionId], CancellationToken.None);

        _tenant.Clears.Should().BeEmpty();
    }

    [Theory]
    [InlineData("acknowledged")]
    [InlineData("snoozed into the future")]
    [InlineData("suppressed by do not disturb")]
    [InlineData("of a disabled rule")]
    public async Task An_excursion_that_does_not_sound_does_not_hold_back_the_all_clear(string quiet)
    {
        var closing = await _tenant.SeedAsync(AlertRuleSeverity.Critical);
        await (quiet switch
        {
            "acknowledged" => _tenant.SeedAsync(acknowledged: true),
            "snoozed into the future" => _tenant.SeedAsync(snoozedUntil: AlarmTestTenant.Now.AddMinutes(10)),
            "suppressed by do not disturb" => _tenant.SeedAsync(suppressionReason: "quiet_hours"),
            _ => _tenant.SeedAsync(ruleEnabled: false),
        });
        await _tenant.ResolveAsync(closing.ExcursionId);

        await _tenant.Relay().ClearIfQuietAsync([closing.ExcursionId], CancellationToken.None);

        _tenant.Clears.Should().ContainSingle();
    }

    [Theory]
    [InlineData("whose snooze has lapsed")]
    [InlineData("in hysteresis")]
    public async Task An_open_excursion_holds_back_the_all_clear_and_is_replayed(string sounding)
    {
        var closing = await _tenant.SeedAsync(AlertRuleSeverity.Warning, "High");
        var open = await (sounding == "in hysteresis"
            ? _tenant.SeedAsync(inHysteresis: true)
            : _tenant.SeedAsync(snoozedUntil: AlarmTestTenant.Now.AddMinutes(-1), snoozeCount: 1));
        await _tenant.ResolveAsync(closing.ExcursionId);
        var relay = _tenant.Relay();

        await relay.ClearIfQuietAsync([closing.ExcursionId], CancellationToken.None);

        _tenant.Clears.Should().BeEmpty();
        (await relay.HighestAlarmingAsync(CancellationToken.None))!
            .Notify.Group.Should().Be(open.ExcursionId.ToString());
    }

    [Fact]
    public async Task A_change_to_an_excursion_that_raised_no_alarm_sends_no_all_clear()
    {
        var info = await _tenant.SeedAsync(AlertRuleSeverity.Info, "Note");
        var test = await _tenant.SeedAsync(AlertRuleSeverity.Critical, isTest: true);
        await _tenant.ResolveAsync(info.ExcursionId);
        await _tenant.ResolveAsync(test.ExcursionId);

        await _tenant.Relay().ClearIfQuietAsync([info.ExcursionId, test.ExcursionId], CancellationToken.None);

        _tenant.Clears.Should().BeEmpty();
    }

    [Fact]
    public async Task A_new_subscriber_hears_only_the_most_severe_sounding_alarm()
    {
        await _tenant.SeedAsync(AlertRuleSeverity.Warning, "High", triggeredAt: AlarmTestTenant.Now.AddMinutes(-1));
        await _tenant.SeedAsync(AlertRuleSeverity.Critical, "Older urgent low", triggeredAt: AlarmTestTenant.Now.AddMinutes(-40));
        var newest = await _tenant.SeedAsync(AlertRuleSeverity.Critical, "Urgent low", triggeredAt: AlarmTestTenant.Now.AddMinutes(-10));
        await _tenant.SeedAsync(AlertRuleSeverity.Critical, "Snoozed", triggeredAt: AlarmTestTenant.Now.AddMinutes(-2),
            snoozedUntil: AlarmTestTenant.Now.AddMinutes(10));
        _tenant.LatestReading(mgdl: 50);

        var alarm = await _tenant.Relay().HighestAlarmingAsync(CancellationToken.None);

        alarm!.EventName.Should().Be(EngineAlarmEvents.UrgentAlarm);
        alarm.Notify.Group.Should().Be(newest.ExcursionId.ToString());
        alarm.Notify.Title.Should().Be("Urgent low");
        alarm.Notify.Message.Should().Be("BG Now: 50 mg/dL");
    }

    [Fact]
    public async Task A_stale_reading_is_left_out_of_the_replayed_alarm()
    {
        await _tenant.SeedAsync(AlertRuleSeverity.Critical);
        _tenant.LatestReading(mgdl: 50, minutesAgo: 30);

        var alarm = await _tenant.Relay().HighestAlarmingAsync(CancellationToken.None);

        alarm!.Notify.Message.Should().Be("Urgent low");
    }

    [Fact]
    public async Task A_new_subscriber_hears_nothing_when_nothing_sounds()
    {
        await _tenant.SeedAsync(AlertRuleSeverity.Critical, acknowledged: true);
        await _tenant.SeedAsync(AlertRuleSeverity.Critical, resolved: true);
        await _tenant.SeedAsync(AlertRuleSeverity.Info, "Note");

        (await _tenant.Relay().HighestAlarmingAsync(CancellationToken.None)).Should().BeNull();
    }
}

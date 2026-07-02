using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Monitoring;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;
using Xunit;

namespace Nocturne.API.Tests.Services.Monitoring;

[Trait("Category", "Unit")]
public class TrackerAlertRuleSyncServiceTests
{
    private readonly DbContextOptions<NocturneDbContext> _options;
    private readonly TrackerAlertRuleSyncService _sut;
    private readonly Guid _tenantId = Guid.NewGuid();

    public TrackerAlertRuleSyncServiceTests()
    {
        _options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase($"tracker_rule_sync_tests_{Guid.NewGuid()}")
            .Options;
        using (var db = new NocturneDbContext(_options))
        {
            db.Database.EnsureCreated();
        }

        var factory = new TestTenantDbContextFactory(_options, _tenantId);

        var classifier = new Mock<IRuleScopeClassifier>();
        classifier
            .Setup(c => c.Classify(It.IsAny<AlertConditionType>(), It.IsAny<string>()))
            .Returns(RuleScopeClass.Undirected);

        _referenceService
            .Setup(r => r.FindReferencingRulesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Guid>());

        _sut = new TrackerAlertRuleSyncService(
            factory,
            classifier.Object,
            _referenceService.Object,
            NullLogger<TrackerAlertRuleSyncService>.Instance);
    }

    private readonly Mock<IAlertReferenceService> _referenceService = new();

    private NocturneDbContext Db() => new(_options) { TenantId = _tenantId };

    private async Task<TrackerDefinitionEntity> SeedDefinitionAsync(
        TrackerMode mode = TrackerMode.Duration,
        int? lifespanHours = 240,
        params TrackerNotificationThresholdEntity[] thresholds)
    {
        await using var db = Db();
        var definition = new TrackerDefinitionEntity
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            UserId = "user-1",
            Name = "Sensor",
            Mode = mode,
            LifespanHours = lifespanHours,
        };
        foreach (var threshold in thresholds)
        {
            threshold.Id = threshold.Id == Guid.Empty ? Guid.NewGuid() : threshold.Id;
            threshold.TenantId = _tenantId;
            threshold.TrackerDefinitionId = definition.Id;
            definition.NotificationThresholds.Add(threshold);
        }
        db.TrackerDefinitions.Add(definition);
        await db.SaveChangesAsync();
        return definition;
    }

    private static TrackerNotificationThresholdEntity Threshold(
        int hours,
        NotificationUrgency urgency = NotificationUrgency.Warn,
        bool push = false,
        bool respectQuietHours = true,
        string? description = null) => new()
    {
        Hours = hours,
        Urgency = urgency,
        PushEnabled = push,
        RespectQuietHours = respectQuietHours,
        Description = description,
    };

    private static JsonElement Params(AlertRuleEntity rule) =>
        JsonSerializer.Deserialize<JsonElement>(rule.ConditionParams);

    [Fact]
    public async Task Sync_creates_one_managed_rule_per_threshold_with_baked_minutes()
    {
        var definition = await SeedDefinitionAsync(
            lifespanHours: 240,
            thresholds: [Threshold(228), Threshold(-12, NotificationUrgency.Urgent, push: true)]);

        await _sut.SyncDefinitionAsync(definition.Id);

        await using var db = Db();
        var rules = await db.AlertRules.Include(r => r.Channels).OrderBy(r => r.Name).ToListAsync();
        rules.Should().HaveCount(2);
        rules.Should().OnlyContain(r =>
            r.ManagedBy == $"tracker:{definition.Id}"
            && r.ConditionType == AlertConditionType.TrackerAge
            && r.IsEnabled);

        // Positive threshold: hours * 60. Negative duration threshold: (lifespan + hours) * 60.
        foreach (var rule in rules)
        {
            var p = Params(rule);
            p.GetProperty("tracker_definition_id").GetGuid().Should().Be(definition.Id);
            p.GetProperty("operator").GetString().Should().Be(">=");
            p.GetProperty("minutes").GetInt32().Should().Be(228 * 60);
        }

        var thresholds = await db.TrackerNotificationThresholds.ToListAsync();
        thresholds.Should().OnlyContain(t => t.AlertRuleId != null);
    }

    [Fact]
    public async Task Sync_maps_urgency_to_severity_and_push_to_channels()
    {
        var definition = await SeedDefinitionAsync(
            thresholds:
            [
                Threshold(1, NotificationUrgency.Info),
                Threshold(2, NotificationUrgency.Urgent, push: true, respectQuietHours: false),
            ]);

        await _sut.SyncDefinitionAsync(definition.Id);

        await using var db = Db();
        var rules = await db.AlertRules.Include(r => r.Channels).ToListAsync();

        var info = rules.Single(r => Params(r).GetProperty("minutes").GetInt32() == 60);
        info.Severity.Should().Be(AlertRuleSeverity.Info);
        info.AllowThroughDnd.Should().BeFalse();
        info.Channels.Select(c => c.ChannelType).Should().Equal(ChannelType.InApp);

        var urgent = rules.Single(r => Params(r).GetProperty("minutes").GetInt32() == 120);
        urgent.Severity.Should().Be(AlertRuleSeverity.Critical);
        urgent.AllowThroughDnd.Should().BeTrue();
        urgent.Channels.Select(c => c.ChannelType)
            .Should().BeEquivalentTo([ChannelType.InApp, ChannelType.WebPush]);
    }

    [Fact]
    public async Task Sync_event_mode_keeps_negative_minutes_relative_to_schedule()
    {
        var definition = await SeedDefinitionAsync(
            mode: TrackerMode.Event,
            lifespanHours: null,
            thresholds: [Threshold(-24)]);

        await _sut.SyncDefinitionAsync(definition.Id);

        await using var db = Db();
        var rule = await db.AlertRules.SingleAsync();
        Params(rule).GetProperty("minutes").GetInt32().Should().Be(-24 * 60);
    }

    [Fact]
    public async Task Sync_skips_negative_duration_threshold_without_lifespan()
    {
        var definition = await SeedDefinitionAsync(
            lifespanHours: null,
            thresholds: [Threshold(-12)]);

        await _sut.SyncDefinitionAsync(definition.Id);

        await using var db = Db();
        (await db.AlertRules.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Resync_preserves_user_edited_channels_and_enabled_state()
    {
        var definition = await SeedDefinitionAsync(thresholds: [Threshold(24)]);
        await _sut.SyncDefinitionAsync(definition.Id);

        Guid ruleId;
        await using (var db = Db())
        {
            var rule = await db.AlertRules.Include(r => r.Channels).SingleAsync();
            ruleId = rule.Id;
            rule.IsEnabled = false;
            rule.Channels.Add(new AlertRuleChannelEntity
            {
                Id = Guid.NewGuid(),
                TenantId = _tenantId,
                AlertRuleId = rule.Id,
                ChannelType = ChannelType.Telegram,
                Destination = "@someone",
                SortOrder = 2,
            });
            await db.SaveChangesAsync();
        }

        // Change the threshold hours and re-sync: condition is overwritten, user edits stay.
        await using (var db = Db())
        {
            var threshold = await db.TrackerNotificationThresholds.SingleAsync();
            threshold.Hours = 48;
            await db.SaveChangesAsync();
        }
        await _sut.SyncDefinitionAsync(definition.Id);

        await using (var db = Db())
        {
            var rule = await db.AlertRules.Include(r => r.Channels).SingleAsync();
            rule.Id.Should().Be(ruleId);
            Params(rule).GetProperty("minutes").GetInt32().Should().Be(48 * 60);
            rule.IsEnabled.Should().BeFalse();
            rule.Channels.Should().Contain(c => c.ChannelType == ChannelType.Telegram);
        }
    }

    [Fact]
    public async Task Resync_adopts_orphaned_rules_after_wholesale_threshold_replacement()
    {
        // The threshold editor replaces the whole list with fresh rows (new ids, null
        // AlertRuleId); an unchanged threshold must keep its rule and channel edits.
        var definition = await SeedDefinitionAsync(thresholds: [Threshold(24)]);
        await _sut.SyncDefinitionAsync(definition.Id);

        Guid originalRuleId;
        await using (var db = Db())
        {
            var rule = await db.AlertRules.Include(r => r.Channels).SingleAsync();
            originalRuleId = rule.Id;
            rule.Channels.Add(new AlertRuleChannelEntity
            {
                Id = Guid.NewGuid(),
                TenantId = _tenantId,
                AlertRuleId = rule.Id,
                ChannelType = ChannelType.Webhook,
                Destination = "https://example.test/hook",
                SortOrder = 5,
            });

            var old = await db.TrackerNotificationThresholds.SingleAsync();
            db.TrackerNotificationThresholds.Remove(old);
            db.TrackerNotificationThresholds.Add(new TrackerNotificationThresholdEntity
            {
                Id = Guid.NewGuid(),
                TenantId = _tenantId,
                TrackerDefinitionId = definition.Id,
                Hours = 24,
                Urgency = NotificationUrgency.Warn,
            });
            await db.SaveChangesAsync();
        }

        await _sut.SyncDefinitionAsync(definition.Id);

        await using (var db = Db())
        {
            var rule = await db.AlertRules.Include(r => r.Channels).SingleAsync();
            rule.Id.Should().Be(originalRuleId);
            rule.Channels.Should().Contain(c => c.ChannelType == ChannelType.Webhook);
            (await db.TrackerNotificationThresholds.SingleAsync()).AlertRuleId.Should().Be(originalRuleId);
        }
    }

    [Fact]
    public async Task Resync_deletes_rules_for_removed_thresholds()
    {
        var definition = await SeedDefinitionAsync(thresholds: [Threshold(24), Threshold(48)]);
        await _sut.SyncDefinitionAsync(definition.Id);

        await using (var db = Db())
        {
            var removed = await db.TrackerNotificationThresholds.FirstAsync(t => t.Hours == 24);
            db.TrackerNotificationThresholds.Remove(removed);
            await db.SaveChangesAsync();
        }

        await _sut.SyncDefinitionAsync(definition.Id);

        await using (var db2 = Db())
        {
            var rules = await db2.AlertRules.ToListAsync();
            rules.Should().HaveCount(1);
            Params(rules[0]).GetProperty("minutes").GetInt32().Should().Be(48 * 60);
        }
    }

    [Fact]
    public async Task DeleteRulesForDefinition_removes_all_managed_rules()
    {
        var definition = await SeedDefinitionAsync(thresholds: [Threshold(24), Threshold(48)]);
        await _sut.SyncDefinitionAsync(definition.Id);

        await _sut.DeleteRulesForDefinitionAsync(definition.Id);

        await using var db = Db();
        (await db.AlertRules.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Resync_edited_hours_updates_the_rule_in_place_via_positional_adoption()
    {
        // Wholesale replacement AND changed hours: the rule must be updated, not
        // delete+recreated (deletion cascades the open excursion and drops channel edits).
        var definition = await SeedDefinitionAsync(thresholds: [Threshold(24)]);
        await _sut.SyncDefinitionAsync(definition.Id);

        Guid originalRuleId;
        await using (var db = Db())
        {
            originalRuleId = (await db.AlertRules.SingleAsync()).Id;
            var old = await db.TrackerNotificationThresholds.SingleAsync();
            db.TrackerNotificationThresholds.Remove(old);
            db.TrackerNotificationThresholds.Add(new TrackerNotificationThresholdEntity
            {
                Id = Guid.NewGuid(),
                TenantId = _tenantId,
                TrackerDefinitionId = definition.Id,
                Hours = 36,
                Urgency = NotificationUrgency.Warn,
            });
            await db.SaveChangesAsync();
        }

        await _sut.SyncDefinitionAsync(definition.Id);

        await using (var db = Db())
        {
            var rule = await db.AlertRules.SingleAsync();
            rule.Id.Should().Be(originalRuleId);
            Params(rule).GetProperty("minutes").GetInt32().Should().Be(36 * 60);
        }
    }

    [Fact]
    public async Task Orphan_referenced_by_alert_state_rule_is_disabled_not_deleted()
    {
        var definition = await SeedDefinitionAsync(thresholds: [Threshold(24)]);
        await _sut.SyncDefinitionAsync(definition.Id);

        Guid ruleId;
        await using (var db = Db())
        {
            ruleId = (await db.AlertRules.SingleAsync()).Id;
            db.TrackerNotificationThresholds.RemoveRange(await db.TrackerNotificationThresholds.ToListAsync());
            await db.SaveChangesAsync();
        }

        _referenceService
            .Setup(r => r.FindReferencingRulesAsync(ruleId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Guid.NewGuid()]);

        await _sut.SyncDefinitionAsync(definition.Id);

        await using (var db = Db())
        {
            var rule = await db.AlertRules.SingleAsync();
            rule.Id.Should().Be(ruleId);
            rule.IsEnabled.Should().BeFalse();
        }
    }

    private sealed class TestTenantDbContextFactory(
        DbContextOptions<NocturneDbContext> options, Guid tenantId) : ITenantDbContextFactory
    {
        public ValueTask<NocturneDbContext> CreateAsync(CancellationToken ct = default) =>
            ValueTask.FromResult(new NocturneDbContext(options) { TenantId = tenantId });
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Identity;
using Nocturne.API.Services.Realtime;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Configuration;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.Tests.Shared.Mocks;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// One tenant's alert state in an in-memory database, with a real <see cref="EngineAlarmRelay"/> over
/// it and the AlarmHub events it sends recorded.
/// </summary>
internal sealed class AlarmTestTenant
{
    public static readonly Guid Tenant = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    public static readonly DateTime Now = new(2026, 10, 24, 3, 0, 0, DateTimeKind.Utc);

    private sealed class SharedInMemoryFactory(string dbName, Guid tenantId) : IDbContextFactory<NocturneDbContext>
    {
        public NocturneDbContext CreateDbContext()
        {
            var ctx = TestDbContextFactory.CreateInMemoryContext(dbName);
            ctx.TenantId = tenantId;
            return ctx;
        }
    }

    private readonly string _dbName = $"engine_alarm_{Guid.NewGuid()}";

    public AlarmTestTenant()
    {
        Contexts = new SharedInMemoryFactory(_dbName, Tenant);
        Broadcast
            .Setup(b => b.BroadcastEngineAlarmAsync(It.IsAny<string>(), It.IsAny<NotificationBase>()))
            .Callback((string eventName, NotificationBase notify) => Sent.Add(new EngineAlarm(eventName, notify)))
            .Returns(Task.CompletedTask);
        Canonical.Setup(c => c.GetLatestAsync(It.IsAny<CancellationToken>())).ReturnsAsync((SensorGlucose?)null);
        Units(null);
    }

    public IDbContextFactory<NocturneDbContext> Contexts { get; }
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(Now));
    public Mock<ISignalRBroadcastService> Broadcast { get; } = new();
    public Mock<ICanonicalGlucoseService> Canonical { get; } = new();
    public Mock<ITenantAppearanceReader> Appearance { get; } = new();
    public Mock<ITenantService> SetupDefaults { get; } = new();
    public List<EngineAlarm> Sent { get; } = [];

    public IEnumerable<EngineAlarm> Clears => Sent.Where(a => a.EventName == EngineAlarmEvents.ClearAlarm);

    public ITenantAccessor Accessor => MockTenantAccessor.Create(Tenant).Object;

    public EngineAlarmRelay Relay() => new(
        Contexts.AsNocturneFactory(),
        Accessor,
        new TenantGlucoseUnits(Accessor, Appearance.Object, SetupDefaults.Object),
        Canonical.Object,
        Broadcast.Object,
        Time,
        NullLogger<EngineAlarmRelay>.Instance);

    public void Units(string? units) =>
        Appearance
            .Setup(a => a.GetTenantAppearanceAsync(Tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserDisplayPreferences { GlucoseUnits = units });

    public void SetupDefaultUnits(string units) =>
        SetupDefaults
            .Setup(t => t.GetDefaultGlucoseUnitsAsync(Tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(units);

    public void LatestReading(double mgdl, double minutesAgo = 2) =>
        Canonical
            .Setup(c => c.GetLatestAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SensorGlucose { Timestamp = Now.AddMinutes(-minutesAgo), Mgdl = mgdl });

    public sealed record Seeded(Guid ExcursionId, Guid InstanceId);

    public async Task<Seeded> SeedAsync(
        AlertRuleSeverity severity = AlertRuleSeverity.Critical,
        string name = "Urgent low",
        DateTime? triggeredAt = null,
        DateTime? snoozedUntil = null,
        int snoozeCount = 0,
        bool acknowledged = false,
        bool resolved = false,
        bool isTest = false,
        bool inHysteresis = false,
        string? suppressionReason = null,
        bool ruleEnabled = true,
        Guid? tenantId = null)
    {
        var tenant = tenantId ?? Tenant;
        await using var db = new SharedInMemoryFactory(_dbName, tenant).CreateDbContext();
        var rule = new AlertRuleEntity
        {
            Id = Guid.NewGuid(),
            TenantId = tenant,
            Name = name,
            Severity = severity,
            ConditionType = AlertConditionType.Threshold,
            IsEnabled = ruleEnabled,
        };
        var started = triggeredAt ?? Now.AddMinutes(-30);
        var excursion = new AlertExcursionEntity
        {
            Id = Guid.NewGuid(),
            TenantId = tenant,
            AlertRuleId = rule.Id,
            StartedAt = started,
            AcknowledgedAt = acknowledged ? Now.AddMinutes(-5) : null,
            EndedAt = resolved ? Now.AddMinutes(-1) : null,
            HysteresisStartedAt = inHysteresis ? Now.AddMinutes(-2) : null,
        };
        var instance = new AlertInstanceEntity
        {
            Id = Guid.NewGuid(),
            TenantId = tenant,
            AlertExcursionId = excursion.Id,
            TriggeredAt = started,
            ResolvedAt = resolved ? Now.AddMinutes(-1) : null,
            SnoozedUntil = snoozedUntil,
            SnoozeCount = snoozeCount,
            IsTest = isTest,
            SuppressionReason = suppressionReason,
        };
        db.AlertRules.Add(rule);
        db.AlertExcursions.Add(excursion);
        db.AlertInstances.Add(instance);
        await db.SaveChangesAsync();
        return new Seeded(excursion.Id, instance.Id);
    }

    /// <summary>Closes an excursion as the tracker and <see cref="ExcursionResolutionHandler"/> leave it.</summary>
    public async Task ResolveAsync(Guid excursionId)
    {
        await using var db = await Contexts.CreateDbContextAsync();
        var excursion = await db.AlertExcursions.SingleAsync(e => e.Id == excursionId);
        excursion.EndedAt = Now;
        foreach (var instance in await db.AlertInstances.Where(i => i.AlertExcursionId == excursionId).ToListAsync())
            instance.ResolvedAt = Now;
        await db.SaveChangesAsync();
    }

    public async Task MuteAsync(Guid excursionId)
    {
        await using var db = await Contexts.CreateDbContextAsync();
        db.AlertExcursionMutes.Add(new AlertExcursionMuteEntity
        {
            Id = Guid.NewGuid(),
            TenantId = Tenant,
            SubjectId = Guid.NewGuid(),
            AlertExcursionId = excursionId,
            CreatedAt = Now,
        });
        await db.SaveChangesAsync();
    }

    public async Task<AlertInstanceEntity> InstanceAsync(Guid instanceId)
    {
        await using var db = await Contexts.CreateDbContextAsync();
        return await db.AlertInstances.IgnoreQueryFilters().AsNoTracking().SingleAsync(i => i.Id == instanceId);
    }
}

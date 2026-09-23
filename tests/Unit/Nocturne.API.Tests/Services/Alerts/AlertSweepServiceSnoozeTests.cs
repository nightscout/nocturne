using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Alerts.Engines;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.API.Services.Audit;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// Drives <see cref="AlertSweepService.CheckSnoozedInstancesAsync"/> end to end over mocked
/// persistence and glucose, with the real managed engine and evaluators evaluating snooze
/// conditions.
/// </summary>
[Trait("Category", "Unit")]
public class AlertSweepServiceSnoozeTests
{
    private static readonly DateTime Now = new(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Tenant = Guid.NewGuid();

    private const string LowRule = """{"direction":"below","value":70}""";
    private const string HighRule = """{"direction":"above","value":180}""";

    private readonly Mock<IAlertRepository> _repository = new();
    private readonly Mock<ICanonicalGlucoseService> _canonical = new();
    private readonly List<UpdateAlertInstanceRequest> _updates = [];
    private readonly List<SensorContext> _enricherInputs = [];
    private List<SensorGlucose> _readings = [];

    public AlertSweepServiceSnoozeTests()
    {
        _repository
            .Setup(r => r.GetTenantAlertContextAsync(Tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TenantAlertContext(Tenant, "owner", "slug", "Slug", true, Now.AddMinutes(-1)));
        _repository
            .Setup(r => r.UpdateInstanceAsync(It.IsAny<UpdateAlertInstanceRequest>(), It.IsAny<CancellationToken>()))
            .Callback<UpdateAlertInstanceRequest, CancellationToken>((u, _) => _updates.Add(u))
            .Returns(Task.CompletedTask);
        _canonical
            .Setup(c => c.GetRecentAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _readings.OrderByDescending(r => r.Timestamp).ToList());
    }

    private static SnoozedInstanceSnapshot Instance(
        string clientConfiguration,
        int snoozeCount = 1,
        AlertConditionType type = AlertConditionType.Threshold,
        string conditionParams = LowRule,
        Guid? tenant = null)
        => new(Guid.NewGuid(), tenant ?? Tenant, Guid.NewGuid(), "triggered", snoozeCount,
            Guid.NewGuid(), type, conditionParams, clientConfiguration);

    private static SensorGlucose Reading(double minutesAgo, double mgdl, double? trendRate = null) => new()
    {
        Timestamp = Now.AddMinutes(-minutesAgo),
        Mgdl = mgdl,
        TrendRate = trendRate,
    };

    private void FiveMinuteReadings(double tMinus10, double tMinus5, double latest, double ageMinutes = 1)
        => _readings =
        [
            Reading(ageMinutes + 10, tMinus10),
            Reading(ageMinutes + 5, tMinus5),
            Reading(ageMinutes, latest, trendRate: (latest - tMinus5) / 5),
        ];

    private async Task SweepAsync(params SnoozedInstanceSnapshot[] instances)
    {
        _repository
            .Setup(r => r.GetExpiredSnoozedInstancesAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(instances);

        var enricher = new Mock<ISensorContextEnricher>();
        enricher
            .Setup(e => e.EnrichAsync(
                It.IsAny<SensorContext>(), It.IsAny<IEnumerable<AlertRuleSnapshot>>(),
                It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Callback<SensorContext, IEnumerable<AlertRuleSnapshot>, Guid, CancellationToken>(
                (ctx, _, _, _) => _enricherInputs.Add(ctx))
            .ReturnsAsync((SensorContext ctx, IEnumerable<AlertRuleSnapshot> _, Guid _, CancellationToken _) => ctx);

        var clock = new FakeTimeProvider(new DateTimeOffset(Now));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton(_repository.Object);
        services.AddSingleton(_canonical.Object);
        services.AddSingleton(enricher.Object);
        services.AddSingleton(Mock.Of<IConditionTimerStore>());
        services.AddSingleton(Mock.Of<IExcursionTracker>());
        services.AddScoped<ITenantAccessor>(_ => Mock.Of<ITenantAccessor>());
        services.AddScoped<IAuditContext, AuditContext>();
        services.AddScoped(_ => new NocturneDbContext(
            new DbContextOptionsBuilder<NocturneDbContext>()
                .UseSqlite("DataSource=:memory:")
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
                .Options));
        services.AddAlertEvaluators();
        services.AddScoped<ConditionEvaluatorRegistry>();
        services.AddScoped<IAlertEvaluationEngine, ManagedAlertEngine>();

        await using var provider = services.BuildServiceProvider();
        var sut = new AlertSweepService(provider, NullLogger<AlertSweepService>.Instance, clock);

        await sut.CheckSnoozedInstancesAsync(CancellationToken.None);
    }

    private UpdateAlertInstanceRequest UpdateFor(SnoozedInstanceSnapshot instance)
        => _updates.Should().ContainSingle(u => u.Id == instance.InstanceId).Subject;

    private void ShouldBeExtended(SnoozedInstanceSnapshot instance, int minutes = SmartSnoozeConfig.DefaultExtendMinutes)
    {
        var update = UpdateFor(instance);
        update.SnoozedUntil.Should().Be(Now.AddMinutes(minutes));
        update.SnoozeCount.Should().Be(instance.SnoozeCount + 1);
    }

    private void ShouldBeCleared(SnoozedInstanceSnapshot instance)
    {
        var update = UpdateFor(instance);
        update.SnoozedUntil.Should().Be(DateTime.MinValue);
        update.SnoozeCount.Should().BeNull();
    }

    private const string SmartOn = """{"snooze":{"smartSnooze":true}}""";

    // ---- trend fallback ----

    [Fact]
    public async Task LowSnooze_NoiseLevelRise_IsNotExtended()
    {
        FiveMinuteReadings(60, 60.5, 61);
        var instance = Instance(SmartOn);

        await SweepAsync(instance);

        ShouldBeCleared(instance);
    }

    [Fact]
    public async Task LowSnooze_GenuineRise_IsExtended()
    {
        FiveMinuteReadings(58, 60, 65);
        var instance = Instance("""{"snooze":{"smartSnooze":true,"smartSnoozeExtendMinutes":7}}""");

        await SweepAsync(instance);

        ShouldBeExtended(instance, minutes: 7);
    }

    [Fact]
    public async Task HighSnooze_SmallFall_IsExtended_WhereTheSameRiseWouldNotHoldALow()
    {
        FiveMinuteReadings(200, 200, 198.5);
        var high = Instance(SmartOn, conditionParams: HighRule);
        await SweepAsync(high);
        ShouldBeExtended(high);

        _updates.Clear();
        FiveMinuteReadings(60, 60, 61.5);
        var low = Instance(SmartOn);
        await SweepAsync(low);
        ShouldBeCleared(low);
    }

    [Fact]
    public async Task StaleReadings_ClearTheSnooze()
    {
        FiveMinuteReadings(50, 60, 75, ageMinutes: 20);
        var instance = Instance(SmartOn);

        await SweepAsync(instance);

        ShouldBeCleared(instance);
    }

    [Fact]
    public async Task NoReadings_ClearTheSnooze()
    {
        _readings = [];
        var instance = Instance(SmartOn);

        await SweepAsync(instance);

        ShouldBeCleared(instance);
    }

    [Fact]
    public async Task GlucoseLoadFailure_ClearsTheSnooze()
    {
        _canonical
            .Setup(c => c.GetRecentAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));
        var instance = Instance(SmartOn);

        await SweepAsync(instance);

        ShouldBeCleared(instance);
    }

    [Fact]
    public async Task NonThresholdRuleWithoutConditions_ClearsTheSnooze()
    {
        FiveMinuteReadings(50, 60, 75);
        var instance = Instance(SmartOn, type: AlertConditionType.Composite,
            conditionParams: """{"operator":"and","conditions":[]}""");

        await SweepAsync(instance);

        ShouldBeCleared(instance);
    }

    [Fact]
    public async Task SmartSnoozeOff_ClearsTheSnooze()
    {
        FiveMinuteReadings(50, 60, 75);
        var instance = Instance("""{"snooze":{"smartSnooze":false}}""");

        await SweepAsync(instance);

        ShouldBeCleared(instance);
    }

    // ---- maxCount ----

    [Fact]
    public async Task MaxCountReached_ClearsEvenOnAGenuineRise()
    {
        FiveMinuteReadings(50, 60, 75);
        var instance = Instance("""{"snooze":{"smartSnooze":true,"maxCount":2}}""", snoozeCount: 2);

        await SweepAsync(instance);

        ShouldBeCleared(instance);
    }

    [Fact]
    public async Task MaxCountOmitted_DefaultsToThree()
    {
        FiveMinuteReadings(50, 60, 75);
        var belowCap = Instance(SmartOn, snoozeCount: 2);
        var atCap = Instance(SmartOn, snoozeCount: 3);

        await SweepAsync(belowCap, atCap);

        ShouldBeExtended(belowCap);
        ShouldBeCleared(atCap);
    }

    // ---- snooze conditions see the glucose value ----

    private const string ExtendWhileAbove65 = """
        {"snooze":{"smartSnooze":true,"conditions":[
          {"type":"threshold","threshold":{"direction":"above","value":65}}]}}
        """;

    [Fact]
    public async Task ThresholdCondition_SeesTheFreshCanonicalValue()
    {
        _readings = [Reading(2, 72, trendRate: 1.5)];
        var instance = Instance(ExtendWhileAbove65);

        await SweepAsync(instance);

        ShouldBeExtended(instance);
        var context = _enricherInputs.Should().ContainSingle().Subject;
        context.LatestValue.Should().Be(72m);
        context.LatestTimestamp.Should().Be(Now.AddMinutes(-2));
        context.TrendRate.Should().Be(1.5m);
    }

    [Fact]
    public async Task ThresholdCondition_FalseForTheFreshValue_ClearsTheSnooze()
    {
        _readings = [Reading(2, 60)];
        var instance = Instance(ExtendWhileAbove65);

        await SweepAsync(instance);

        ShouldBeCleared(instance);
    }

    [Fact]
    public async Task ThresholdCondition_OnAStaleReading_ReadsNoValueAndClears()
    {
        _readings = [Reading(20, 90, trendRate: 2)];
        var instance = Instance(ExtendWhileAbove65);

        await SweepAsync(instance);

        ShouldBeCleared(instance);
        var context = _enricherInputs.Should().ContainSingle().Subject;
        context.LatestValue.Should().BeNull();
        context.TrendRate.Should().BeNull();
    }

    [Fact]
    public async Task ThresholdCondition_UsesTheNewestReading()
    {
        _readings = [Reading(7, 50), Reading(2, 72), Reading(12, 40)];
        var instance = Instance(ExtendWhileAbove65);

        await SweepAsync(instance);

        ShouldBeExtended(instance);
    }

    [Fact]
    public async Task ConfiguredConditions_TakePrecedenceOverTheTrendFallback()
    {
        // A genuine rise that the trend fallback would accept, but the user's predicate fails.
        FiveMinuteReadings(40, 50, 60);
        var instance = Instance(ExtendWhileAbove65);

        await SweepAsync(instance);

        ShouldBeCleared(instance);
    }

    [Fact]
    public async Task PredictedCondition_IsUnaffectedByTheGlucoseValue()
    {
        // Predictions are enriched independently of LatestValue; with none available the leaf is
        // false whatever the reading says.
        _readings = [Reading(2, 72)];
        var instance = Instance("""
            {"snooze":{"smartSnooze":true,"conditions":[
              {"type":"predicted","predicted":{"operator":">","value":70,"within_minutes":30}}]}}
            """);

        await SweepAsync(instance);

        ShouldBeCleared(instance);
    }

    // ---- robustness ----

    [Fact]
    public async Task MalformedConfigOnOneRule_DoesNotStopTheOthers()
    {
        FiveMinuteReadings(50, 60, 75);
        var malformed = Instance("""{"snooze":{"smartSnooze":true,"maxCount":"lots"}}""");
        var healthy = Instance(SmartOn);

        await SweepAsync(malformed, healthy);

        ShouldBeExtended(malformed);
        ShouldBeExtended(healthy);
    }

    [Fact]
    public async Task FailureForOneTenant_DoesNotStopAnother()
    {
        var otherTenant = Guid.NewGuid();
        _repository
            .Setup(r => r.GetTenantAlertContextAsync(otherTenant, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("tenant lookup failed"));
        FiveMinuteReadings(50, 60, 75);
        var broken = Instance(SmartOn, tenant: otherTenant);
        var healthy = Instance(SmartOn);

        await SweepAsync(broken, healthy);

        _updates.Should().NotContain(u => u.Id == broken.InstanceId);
        ShouldBeExtended(healthy);
    }
}

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Services.Alerts;
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
/// The context <see cref="AlertSweepService.EvaluateAutoResolveAsync"/> hands the engine for an
/// open auto-resolve excursion.
/// </summary>
[Trait("Category", "Unit")]
public class AlertSweepServiceAutoResolveTests
{
    private static readonly DateTime Now = new(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Tenant = Guid.NewGuid();

    private readonly List<SensorGlucose> _readings = [];
    private readonly List<SensorContext> _evaluated = [];
    private DateTime? _anySourceLastReadingAt = Now.AddMinutes(-1);

    private static SensorGlucose Reading(double minutesAgo, double mgdl) =>
        new() { Timestamp = Now.AddMinutes(-minutesAgo), Mgdl = mgdl };

    private async Task SweepAsync()
    {
        var rule = new AlertRuleSnapshot(
            Guid.NewGuid(), Tenant, "rule", AlertConditionType.Threshold, """{"direction":"below","value":70}""",
            AlertRuleSeverity.Warning, "{}", 0, AutoResolveEnabled: true,
            AutoResolveParams: """{"type":"signal_loss","signal_loss":{"timeout_minutes":30}}""");

        var repository = new Mock<IAlertRepository>();
        repository
            .Setup(r => r.GetAutoResolveExcursionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new AutoResolveExcursionSnapshot(Guid.NewGuid(), Tenant, rule)]);
        repository
            .Setup(r => r.GetTenantAlertContextAsync(Tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new TenantAlertContext(Tenant, "owner", "slug", "Slug", true, _anySourceLastReadingAt));

        var canonical = new Mock<ICanonicalGlucoseService>();
        canonical
            .Setup(c => c.GetLatestAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _readings.MaxBy(r => r.Timestamp));
        canonical
            .Setup(c => c.GetRecentAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime since, CancellationToken _) =>
                _readings.Where(r => r.Timestamp >= since).OrderByDescending(r => r.Timestamp).ToList());

        var enricher = new Mock<ISensorContextEnricher>();
        enricher
            .Setup(e => e.EnrichAsync(
                It.IsAny<SensorContext>(), It.IsAny<IEnumerable<AlertRuleSnapshot>>(),
                It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SensorContext ctx, IEnumerable<AlertRuleSnapshot> _, Guid _, CancellationToken _) => ctx);

        var engine = new Mock<IAlertEvaluationEngine>();
        engine
            .Setup(e => e.EvaluateAutoResolveAsync(
                It.IsAny<AlertRuleSnapshot>(), It.IsAny<SensorContext>(), It.IsAny<CancellationToken>()))
            .Callback<AlertRuleSnapshot, SensorContext, CancellationToken>((_, ctx, _) => _evaluated.Add(ctx))
            .ReturnsAsync(new ExcursionTransition(ExcursionTransitionType.None));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(repository.Object);
        services.AddSingleton(canonical.Object);
        services.AddSingleton(enricher.Object);
        services.AddSingleton(engine.Object);
        services.AddSingleton(Mock.Of<IExcursionResolutionHandler>());
        services.AddScoped<ITenantAccessor>(_ => Mock.Of<ITenantAccessor>());
        services.AddScoped<IAuditContext, AuditContext>();
        services.AddScoped(_ => new NocturneDbContext(
            new DbContextOptionsBuilder<NocturneDbContext>()
                .UseSqlite("DataSource=:memory:")
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
                .Options));

        await using var provider = services.BuildServiceProvider();
        var sut = new AlertSweepService(
            provider, NullLogger<AlertSweepService>.Instance, new FakeTimeProvider(new DateTimeOffset(Now)));

        await sut.EvaluateAutoResolveAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Error_readings_are_not_signal()
    {
        _readings.AddRange([Reading(1, 0), Reading(20, 0), Reading(40, 110)]);

        await SweepAsync();

        var context = _evaluated.Should().ContainSingle().Subject;
        context.LastReadingAt.Should().Be(Now.AddMinutes(-40));
        context.LatestTimestamp.Should().Be(Now.AddMinutes(-40));
        context.LatestValue.Should().BeNull();
    }

    [Fact]
    public async Task A_tenant_that_has_never_had_a_reading_is_cold_start()
    {
        _anySourceLastReadingAt = null;

        await SweepAsync();

        var context = _evaluated.Should().ContainSingle().Subject;
        context.LastReadingAt.Should().BeNull();
        context.LatestTimestamp.Should().BeNull();
    }
}

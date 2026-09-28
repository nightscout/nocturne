using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Analytics;
using Nocturne.API.Services.Glucose;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;
using Xunit;

namespace Nocturne.API.Tests.Services.Analytics;

[Trait("Category", "Unit")]
public class WidgetSummaryServiceStatusTests
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly DbContextOptions<NocturneDbContext> _options =
        new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

    [Fact]
    public async Task Current_isClassifiedAgainstTheTenantsThresholdRules()
    {
        SeedThresholdRule("above", 140, AlertRuleSeverity.Warning);

        var summary = await NewService(Reading(170, minutesAgo: 2)).GetSummaryAsync("user");

        summary.Current!.Status.Should().Be(GlucoseStatus.High,
            "the tenant's above-140 rule, not the configured 180 default, bounds the range");
    }

    [Fact]
    public async Task Current_withNoRules_isClassifiedAgainstTheConfiguredDefaults()
    {
        var summary = await NewService(Reading(170, minutesAgo: 2)).GetSummaryAsync("user");

        summary.Current!.Status.Should().Be(GlucoseStatus.InRange);
    }

    [Fact]
    public async Task Current_olderThanTheStaleWindow_isStale()
    {
        var summary = await NewService(Reading(40, minutesAgo: 30)).GetSummaryAsync("user");

        summary.Current!.Status.Should().Be(GlucoseStatus.Stale);
    }

    [Fact]
    public async Task History_carriesNoStatus()
    {
        var summary = await NewService(Reading(120, minutesAgo: 1), Reading(110, minutesAgo: 6))
            .GetSummaryAsync("user", hours: 1);

        summary.History.Should().ContainSingle().Which.Status.Should().BeNull();
    }

    [Fact]
    public async Task Current_whenThresholdsCannotBeRead_isServedWithoutAStatus()
    {
        var summary = await NewService(
                failingContext: true, Reading(120, minutesAgo: 1))
            .GetSummaryAsync("user");

        summary.Current.Should().NotBeNull();
        summary.Current!.Status.Should().BeNull();
    }

    [Theory]
    [InlineData("mbg")]
    [InlineData("cal")]
    public async Task Current_isTheNewestSgv_whenANewerNonSensorEntryExists(string type)
    {
        var newestSgv = Reading(130, minutesAgo: 3);
        newestSgv.Delta = 12;
        newestSgv.Direction = "SingleUp";
        var meter = new Entry
        {
            Id = Guid.NewGuid().ToString(),
            Type = type,
            Mgdl = 250,
            Mbg = 250,
            Mills = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds(),
        };

        var summary = await NewService(meter, newestSgv, Reading(118, minutesAgo: 8))
            .GetSummaryAsync("user", hours: 1);

        summary.Current!.Mills.Should().Be(newestSgv.Mills);
        summary.Current.Sgv.Should().Be(130);
        summary.Current.Delta.Should().Be(12, "the delta is the sensor reading's own, against the previous sgv");
        summary.Current.Direction.Should().Be(Direction.SingleUp);
        summary.History.Should().ContainSingle().Which.Sgv.Should().Be(118);
    }

    private static Entry Reading(double sgv, int minutesAgo) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Type = "sgv",
        Sgv = sgv,
        Mills = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo).ToUnixTimeMilliseconds(),
    };

    private void SeedThresholdRule(string direction, int value, AlertRuleSeverity severity)
    {
        using var db = new NocturneDbContext(_options) { TenantId = _tenantId };
        db.AlertRules.Add(new AlertRuleEntity
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            Severity = severity,
            IsEnabled = true,
            ConditionType = AlertConditionType.Threshold,
            ConditionParams = $$"""{"direction":"{{direction}}","value":{{value}}}""",
        });
        db.SaveChanges();
    }

    private WidgetSummaryService NewService(params Entry[] newestFirst) =>
        NewService(failingContext: false, newestFirst);

    private WidgetSummaryService NewService(bool failingContext, params Entry[] newestFirst)
    {
        var entries = new Mock<IEntryService>();
        entries.Setup(e => e.GetEntriesAsync(
                It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string? type, int count, int _, CancellationToken _) =>
                newestFirst.Where(e => type is null || e.Type == type).Take(count));

        var trackers = new Mock<ITrackerRepository>();
        trackers.Setup(t => t.GetActiveInstancesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var contextFactory = new Mock<ITenantDbContextFactory>();
        contextFactory.Setup(f => f.CreateAsync(It.IsAny<CancellationToken>()))
            .Returns(() => failingContext
                ? ValueTask.FromException<NocturneDbContext>(new InvalidOperationException("boom"))
                : ValueTask.FromResult(new NocturneDbContext(_options) { TenantId = _tenantId }));

        return new WidgetSummaryService(
            entries.Object,
            Mock.Of<IIobCalculator>(),
            Mock.Of<ICobCalculator>(),
            Mock.Of<IBolusRepository>(),
            Mock.Of<ICarbIntakeRepository>(),
            Mock.Of<ITempBasalRepository>(),
            Mock.Of<IApsSnapshotRepository>(),
            trackers.Object,
            Mock.Of<INotificationV1Service>(),
            new GlucoseStatusClassifier(
                new ConfigurationBuilder().Build(), NullLogger<GlucoseStatusClassifier>.Instance),
            contextFactory.Object,
            NullLogger<WidgetSummaryService>.Instance);
    }
}

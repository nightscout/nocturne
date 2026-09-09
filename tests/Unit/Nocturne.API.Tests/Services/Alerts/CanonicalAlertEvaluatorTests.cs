using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Alerts;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// The watermark contract: a canonical reading is evaluated once, and the pass that was skipped
/// is the repeat, never the reading.
/// </summary>
[Trait("Category", "Unit")]
public class CanonicalAlertEvaluatorTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTime Reading = new(2026, 9, 9, 7, 15, 0, DateTimeKind.Utc);

    private readonly Mock<ICanonicalGlucoseService> _canonical = new();
    private readonly Mock<IAlertOrchestrator> _orchestrator = new();
    private readonly AlertEvaluationWatermark _watermark = new();

    private static SensorGlucose Latest(DateTime timestamp, double mgdl = 120, double? trendRate = 0.5) =>
        new() { Timestamp = timestamp, Mgdl = mgdl, TrendRate = trendRate };

    private CanonicalAlertEvaluator Evaluator(Guid tenantId) =>
        new(_canonical.Object,
            _orchestrator.Object,
            Mock.Of<ITenantAccessor>(a => a.TenantId == tenantId),
            _watermark,
            NullLogger<CanonicalAlertEvaluator>.Instance);

    private void LatestIs(params SensorGlucose?[] readings)
    {
        var queue = new Queue<SensorGlucose?>(readings);
        _canonical
            .Setup(c => c.GetLatestAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => queue.Count > 1 ? queue.Dequeue() : queue.Peek());
    }

    private void VerifyPasses(int times) =>
        _orchestrator.Verify(
            o => o.EvaluateAsync(It.IsAny<SensorContext>(), It.IsAny<CancellationToken>()),
            Times.Exactly(times));

    [Fact]
    public async Task EvaluateAsync_EvaluatesTheFirstTimeAReadingIsSeen()
    {
        LatestIs(Latest(Reading));

        await Evaluator(Tenant).EvaluateAsync();

        VerifyPasses(1);
    }

    [Fact]
    public async Task EvaluateAsync_SkipsARepeatOfTheSameReading()
    {
        // A sync publishes in chunks and an uploader re-posts stored readings, so the same
        // canonical reading arrives here many times over.
        LatestIs(Latest(Reading));
        var evaluator = Evaluator(Tenant);

        await evaluator.EvaluateAsync();
        await evaluator.EvaluateAsync();
        await evaluator.EvaluateAsync();

        VerifyPasses(1);
    }

    [Fact]
    public async Task EvaluateAsync_SkipsARepeatAcrossScopes()
    {
        // The evaluator is scoped and a connector's chunks span scopes, so the watermark has to
        // outlive the instance that recorded it.
        LatestIs(Latest(Reading));

        await Evaluator(Tenant).EvaluateAsync();
        await Evaluator(Tenant).EvaluateAsync();

        VerifyPasses(1);
    }

    [Fact]
    public async Task EvaluateAsync_EvaluatesAgainWhenTheStreamAdvances()
    {
        LatestIs(Latest(Reading), Latest(Reading.AddMinutes(5)));
        var evaluator = Evaluator(Tenant);

        await evaluator.EvaluateAsync();
        await evaluator.EvaluateAsync();

        VerifyPasses(2);
    }

    [Fact]
    public async Task EvaluateAsync_EvaluatesAgainWhenTheValueIsCorrectedAtTheSameTimestamp()
    {
        LatestIs(Latest(Reading, mgdl: 120), Latest(Reading, mgdl: 250));
        var evaluator = Evaluator(Tenant);

        await evaluator.EvaluateAsync();
        await evaluator.EvaluateAsync();

        VerifyPasses(2);
    }

    [Fact]
    public async Task EvaluateAsync_EvaluatesAgainWhenOnlyTheTrendRateChanges()
    {
        LatestIs(Latest(Reading, trendRate: 0.5), Latest(Reading, trendRate: -3.5));
        var evaluator = Evaluator(Tenant);

        await evaluator.EvaluateAsync();
        await evaluator.EvaluateAsync();

        VerifyPasses(2);
    }

    [Fact]
    public async Task EvaluateAsync_EvaluatesAgainWhenAnEarlierReadingBecomesLatest()
    {
        // A delete of the newest row moves the canonical latest backwards; that is a different
        // decision, not a repeat of one already made.
        LatestIs(Latest(Reading), Latest(Reading.AddMinutes(-5)));
        var evaluator = Evaluator(Tenant);

        await evaluator.EvaluateAsync();
        await evaluator.EvaluateAsync();

        VerifyPasses(2);
    }

    [Fact]
    public async Task EvaluateAsync_RetriesAReadingWhosePassFailed()
    {
        LatestIs(Latest(Reading));
        _orchestrator
            .SetupSequence(o => o.EvaluateAsync(It.IsAny<SensorContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("engine unavailable"))
            .Returns(Task.CompletedTask);
        var evaluator = Evaluator(Tenant);

        await evaluator.EvaluateAsync();
        await evaluator.EvaluateAsync();

        VerifyPasses(2);
    }

    [Fact]
    public async Task EvaluateAsync_KeepsAWatermarkPerTenant()
    {
        var other = Guid.Parse("22222222-2222-2222-2222-222222222222");
        LatestIs(Latest(Reading));

        await Evaluator(Tenant).EvaluateAsync();
        await Evaluator(other).EvaluateAsync();

        VerifyPasses(2);
    }

    [Fact]
    public async Task EvaluateAsync_DoesNotSkipWhenNoTenantIsInScope()
    {
        // With no tenant the orchestrator returns before its first read, so there is nothing to
        // remember and nothing a shared watermark could correctly stand for.
        LatestIs(Latest(Reading));

        await Evaluator(Guid.Empty).EvaluateAsync();
        await Evaluator(Guid.Empty).EvaluateAsync();

        VerifyPasses(2);
    }

    [Fact]
    public async Task EvaluateAsync_DoesNotEvaluateWithoutAPositiveReading()
    {
        LatestIs(Latest(Reading, mgdl: 0));

        await Evaluator(Tenant).EvaluateAsync();

        VerifyPasses(0);
    }

    [Fact]
    public async Task EvaluateAsync_PassesTheCanonicalReadingIntoTheContext()
    {
        LatestIs(Latest(Reading, mgdl: 187, trendRate: -1.5));
        SensorContext? seen = null;
        _orchestrator
            .Setup(o => o.EvaluateAsync(It.IsAny<SensorContext>(), It.IsAny<CancellationToken>()))
            .Callback<SensorContext, CancellationToken>((c, _) => seen = c)
            .Returns(Task.CompletedTask);

        await Evaluator(Tenant).EvaluateAsync();

        seen.Should().NotBeNull();
        seen!.LatestValue.Should().Be(187m);
        seen.LatestTimestamp.Should().Be(Reading);
        seen.TrendRate.Should().Be(-1.5m);
        seen.LastReadingAt.Should().Be(Reading);
    }
}

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.Glucose;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Xunit;

namespace Nocturne.API.Tests.Services.Glucose;

[Trait("Category", "Unit")]
public class GlucoseStatusClassifierTests
{
    private static readonly TenantOverviewThresholds Defaults = new(55, 80, 180, 260);

    // ---- threshold resolution ----

    [Fact]
    public async Task ResolveThresholdsAsync_noVisibleRules_returnsTheConfiguredDefaults()
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new NocturneDbContext(options) { TenantId = Guid.NewGuid() };
        var classifier = new GlucoseStatusClassifier(
            new ConfigurationBuilder().Build(), NullLogger<GlucoseStatusClassifier>.Instance);

        var resolved = await classifier.ResolveThresholdsAsync(db, CancellationToken.None);

        resolved.Should().Be(classifier.Defaults);
        resolved.Should().Be(Defaults, "the fixture's defaults mirror the configured constants");
    }

    [Fact]
    public void ResolveThresholds_noRules_returnsDefaults()
    {
        GlucoseStatusClassifier.ResolveThresholds(Defaults, []).Should().Be(Defaults);
    }

    [Fact]
    public void ResolveThresholds_overridesEachBucketFromRules()
    {
        var resolved = GlucoseStatusClassifier.ResolveThresholds(Defaults,
        [
            ("below", 60, AlertRuleSeverity.Critical),
            ("below", 90, AlertRuleSeverity.Warning),
            ("above", 170, AlertRuleSeverity.Warning),
            ("above", 250, AlertRuleSeverity.Critical),
        ]);

        resolved.Should().Be(new TenantOverviewThresholds(60, 90, 170, 250));
    }

    [Fact]
    public void ResolveThresholds_infoSeverityFillsTheNonUrgentBuckets()
    {
        var resolved = GlucoseStatusClassifier.ResolveThresholds(Defaults,
        [
            ("below", 85, AlertRuleSeverity.Info),
            ("above", 190, AlertRuleSeverity.Info),
        ]);

        resolved.Should().Be(new TenantOverviewThresholds(55, 85, 190, 260));
    }

    [Fact]
    public void ResolveThresholds_multipleRulesInABucket_mostConservativeWins()
    {
        var resolved = GlucoseStatusClassifier.ResolveThresholds(Defaults,
        [
            // below: highest value is most conservative
            ("below", 70, AlertRuleSeverity.Warning),
            ("below", 85, AlertRuleSeverity.Warning),
            // above: lowest value is most conservative
            ("above", 200, AlertRuleSeverity.Warning),
            ("above", 170, AlertRuleSeverity.Warning),
        ]);

        resolved.Low.Should().Be(85);
        resolved.High.Should().Be(170);
    }

    [Fact]
    public void ResolveThresholds_clampsUrgentBoundsToOrdering()
    {
        var resolved = GlucoseStatusClassifier.ResolveThresholds(Defaults,
        [
            ("below", 90, AlertRuleSeverity.Critical),  // urgent-low above default low (80)
            ("above", 150, AlertRuleSeverity.Critical), // urgent-high below default high (180)
        ]);

        resolved.UrgentLow.Should().BeLessThanOrEqualTo(resolved.Low);
        resolved.UrgentHigh.Should().BeGreaterThanOrEqualTo(resolved.High);
        resolved.UrgentLow.Should().Be(80);
        resolved.UrgentHigh.Should().Be(180);
    }

    [Fact]
    public void ResolveThresholds_invertedBand_lowIsClampedToHigh()
    {
        // A Warning "below 200" with default High=180 would put Low above High.
        var resolved = GlucoseStatusClassifier.ResolveThresholds(Defaults,
            [("below", 200, AlertRuleSeverity.Warning)]);

        resolved.Low.Should().Be(180);
        resolved.High.Should().Be(180);
        resolved.UrgentLow.Should().BeLessThanOrEqualTo(resolved.Low);

        // A reading of 190 must not classify Low.
        GlucoseStatusClassifier.Classify(190, Now.AddMinutes(-1), null, resolved, StaleAfter, Now)
            .Should().Be(GlucoseStatus.High);
    }

    [Fact]
    public void ResolveThresholds_directionCasingIsIgnored()
    {
        var resolved = GlucoseStatusClassifier.ResolveThresholds(Defaults,
            [("Below", 70, AlertRuleSeverity.Critical)]);

        resolved.UrgentLow.Should().Be(70);
    }

    // ---- classification ----

    private static readonly DateTime Now = new(2026, 07, 04, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(25);

    private static GlucoseStatus ClassifyValue(double mgdl) =>
        GlucoseStatusClassifier.Classify(mgdl, Now.AddMinutes(-5), Now.AddMinutes(-5), Defaults, StaleAfter, Now);

    [Theory]
    [InlineData(54, GlucoseStatus.UrgentLow)]
    [InlineData(55, GlucoseStatus.Low)]        // boundary: not urgent
    [InlineData(79, GlucoseStatus.Low)]
    [InlineData(80, GlucoseStatus.InRange)]    // boundary: in range
    [InlineData(120, GlucoseStatus.InRange)]
    [InlineData(180, GlucoseStatus.InRange)]   // boundary: in range
    [InlineData(181, GlucoseStatus.High)]
    [InlineData(260, GlucoseStatus.High)]      // boundary: not urgent
    [InlineData(261, GlucoseStatus.UrgentHigh)]
    public void Classify_valueAgainstThresholds(double mgdl, GlucoseStatus expected)
    {
        ClassifyValue(mgdl).Should().Be(expected);
    }

    [Fact]
    public void Classify_readingOlderThanStaleWindow_isStale()
    {
        GlucoseStatusClassifier.Classify(120, Now.AddMinutes(-26), null, Defaults, StaleAfter, Now)
            .Should().Be(GlucoseStatus.Stale);
    }

    [Fact]
    public void Classify_readingAtExactlyTheStaleWindow_isNotStale()
    {
        GlucoseStatusClassifier.Classify(120, Now - StaleAfter, null, Defaults, StaleAfter, Now)
            .Should().Be(GlucoseStatus.InRange);
    }

    [Fact]
    public void Classify_noReadingAndNoLastReadingAt_isUnknown()
    {
        GlucoseStatusClassifier.Classify(null, null, null, Defaults, StaleAfter, Now)
            .Should().Be(GlucoseStatus.Unknown);
    }

    [Fact]
    public void Classify_noReadingButStaleLastReadingAt_isStale()
    {
        GlucoseStatusClassifier.Classify(null, null, Now.AddHours(-2), Defaults, StaleAfter, Now)
            .Should().Be(GlucoseStatus.Stale);
    }

    [Fact]
    public void Classify_noReadingWithFreshLastReadingAt_isUnknown()
    {
        GlucoseStatusClassifier.Classify(null, null, Now.AddMinutes(-1), Defaults, StaleAfter, Now)
            .Should().Be(GlucoseStatus.Unknown);
    }
}

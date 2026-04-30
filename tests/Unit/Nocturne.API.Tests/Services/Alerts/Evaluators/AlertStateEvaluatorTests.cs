using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Evaluators;

[Trait("Category", "Unit")]
public class AlertStateEvaluatorTests
{
    private static readonly DateTime FixedNow = new(2026, 3, 22, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid TargetAlertId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private readonly AlertStateEvaluator _sut;

    public AlertStateEvaluatorTests()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(FixedNow));
        _sut = new AlertStateEvaluator(timeProvider);
    }

    [Fact]
    public void ConditionType_ShouldBeAlertState()
    {
        _sut.ConditionType.Should().Be(AlertConditionType.AlertState);
    }

    [Fact]
    public async Task MissingAlert_ReturnsFalse()
    {
        var json = $$"""{"alert_id": "{{TargetAlertId}}", "state": "firing"}""";
        var context = MakeContext(new Dictionary<Guid, ActiveAlertSnapshot>());

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task FiringMatch_ReturnsTrue()
    {
        var snapshot = new ActiveAlertSnapshot("firing", FixedNow.AddMinutes(-5), AcknowledgedAt: null);
        var context = MakeContext(new Dictionary<Guid, ActiveAlertSnapshot> { [TargetAlertId] = snapshot });
        var json = $$"""{"alert_id": "{{TargetAlertId}}", "state": "firing"}""";

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task UnacknowledgedMatch_NotYetAcknowledged_ReturnsTrue()
    {
        var snapshot = new ActiveAlertSnapshot("firing", FixedNow.AddMinutes(-5), AcknowledgedAt: null);
        var context = MakeContext(new Dictionary<Guid, ActiveAlertSnapshot> { [TargetAlertId] = snapshot });
        var json = $$"""{"alert_id": "{{TargetAlertId}}", "state": "unacknowledged"}""";

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task UnacknowledgedMatch_AlreadyAcknowledged_ReturnsFalse()
    {
        var snapshot = new ActiveAlertSnapshot("firing", FixedNow.AddMinutes(-5), FixedNow.AddMinutes(-1));
        var context = MakeContext(new Dictionary<Guid, ActiveAlertSnapshot> { [TargetAlertId] = snapshot });
        var json = $$"""{"alert_id": "{{TargetAlertId}}", "state": "unacknowledged"}""";

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task AcknowledgedMatch_ReturnsTrue()
    {
        var snapshot = new ActiveAlertSnapshot("firing", FixedNow.AddMinutes(-10), FixedNow.AddMinutes(-2));
        var context = MakeContext(new Dictionary<Guid, ActiveAlertSnapshot> { [TargetAlertId] = snapshot });
        var json = $$"""{"alert_id": "{{TargetAlertId}}", "state": "acknowledged"}""";

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task AcknowledgedMatch_NeverAcknowledged_ReturnsFalse()
    {
        var snapshot = new ActiveAlertSnapshot("firing", FixedNow.AddMinutes(-10), AcknowledgedAt: null);
        var context = MakeContext(new Dictionary<Guid, ActiveAlertSnapshot> { [TargetAlertId] = snapshot });
        var json = $$"""{"alert_id": "{{TargetAlertId}}", "state": "acknowledged"}""";

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task ForMinutes_Firing_Elapsed_ReturnsTrue()
    {
        var snapshot = new ActiveAlertSnapshot("firing", FixedNow.AddMinutes(-15), AcknowledgedAt: null);
        var context = MakeContext(new Dictionary<Guid, ActiveAlertSnapshot> { [TargetAlertId] = snapshot });
        var json = $$"""{"alert_id": "{{TargetAlertId}}", "state": "firing", "for_minutes": 10}""";

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task ForMinutes_Firing_NotElapsed_ReturnsFalse()
    {
        var snapshot = new ActiveAlertSnapshot("firing", FixedNow.AddMinutes(-5), AcknowledgedAt: null);
        var context = MakeContext(new Dictionary<Guid, ActiveAlertSnapshot> { [TargetAlertId] = snapshot });
        var json = $$"""{"alert_id": "{{TargetAlertId}}", "state": "firing", "for_minutes": 10}""";

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task ForMinutes_Acknowledged_UsesAcknowledgedAt()
    {
        // Triggered 60 min ago, acknowledged 5 min ago; threshold 10 min => not yet elapsed.
        var snapshot = new ActiveAlertSnapshot("firing", FixedNow.AddMinutes(-60), FixedNow.AddMinutes(-5));
        var context = MakeContext(new Dictionary<Guid, ActiveAlertSnapshot> { [TargetAlertId] = snapshot });
        var json = $$"""{"alert_id": "{{TargetAlertId}}", "state": "acknowledged", "for_minutes": 10}""";

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task ForMinutes_Acknowledged_Elapsed_ReturnsTrue()
    {
        var snapshot = new ActiveAlertSnapshot("firing", FixedNow.AddMinutes(-60), FixedNow.AddMinutes(-15));
        var context = MakeContext(new Dictionary<Guid, ActiveAlertSnapshot> { [TargetAlertId] = snapshot });
        var json = $$"""{"alert_id": "{{TargetAlertId}}", "state": "acknowledged", "for_minutes": 10}""";

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task UnknownState_ReturnsFalse()
    {
        var snapshot = new ActiveAlertSnapshot("firing", FixedNow.AddMinutes(-5), AcknowledgedAt: null);
        var context = MakeContext(new Dictionary<Guid, ActiveAlertSnapshot> { [TargetAlertId] = snapshot });
        var json = $$"""{"alert_id": "{{TargetAlertId}}", "state": "smoldering"}""";

        (await _sut.EvaluateAsync(json, context, CancellationToken.None)).Should().BeFalse();
    }

    private static SensorContext MakeContext(IReadOnlyDictionary<Guid, ActiveAlertSnapshot> activeAlerts) => new()
    {
        LatestValue = 100m,
        LatestTimestamp = FixedNow,
        TrendRate = 0m,
        LastReadingAt = FixedNow,
        ActiveAlerts = activeAlerts
    };
}

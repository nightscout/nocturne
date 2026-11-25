using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Nocturne.Alerts.Corpus;
using Nocturne.Core.Alerts.Native;
using Xunit;

namespace Nocturne.Alerts.Native.Tests;

/// <summary>
/// A library that does not report <c>next_change_at</c> leaves every next change unknown: an
/// omitted field must never read as "nothing changes" (docs/alerts/engine-semantics.md §5.3).
/// </summary>
public class NextChangeProbeTests
{
    [NativeFact]
    public void The_loaded_library_reports_next_changes()
    {
        RustAlertEngine.ReportsNextChange().Should().BeTrue();
    }

    [NativeFact]
    public void A_library_that_omits_the_field_does_not()
    {
        RustAlertEngine.ReportsNextChange(request => WithoutNextChange(AlertsInterop.Evaluate(request)))
            .Should().BeFalse();
    }

    [Fact]
    public void A_library_that_rejects_the_probe_does_not()
    {
        RustAlertEngine.ReportsNextChange(_ => """{"schema_version":1,"ok":false,"error":"unknown condition_type"}""")
            .Should().BeFalse();
    }

    /// <summary>
    /// The hostile review's counterexample: a sensor age due in year 10240, which .NET cannot
    /// hold, parses as no next change rather than failing the rule's evaluation.
    /// </summary>
    [NativeFact]
    public void A_change_past_the_host_calendar_parses_as_none()
    {
        var now = new DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc);
        var response = RustAlertEngine.Evaluate(
            new RustAlertRule
            {
                Id = Guid.Empty,
                ConditionType = "sensor_age",
                ConditionParams = JsonSerializer.SerializeToElement(new { @operator = ">", value = 3_000_000 }),
            },
            JsonSerializer.SerializeToElement(new { last_sensor_start_at = now.AddDays(-1) }),
            now);

        var result = RustAlertEngine.GetRuleResult(response);

        result.Root.Should().BeFalse();
        result.NextChangeAt.Should().BeNull();
    }

    private static string WithoutNextChange(string response)
    {
        var node = JsonNode.Parse(response)!;
        node["result"]!.AsObject().Remove("next_change_at");
        return node.ToJsonString();
    }
}

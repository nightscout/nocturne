using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Controllers.V4.Platform;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Platform;

/// <summary>
/// The compatibility analysis DTOs serialize their trace identifier under the legacy
/// <c>correlationId</c> wire name; the attribute carrying that is the entire compat mechanism.
/// </summary>
[Trait("Category", "Unit")]
public class CompatibilityAnalysisWireFormatTests
{
    [Fact]
    public void AnalysisListItemDto_SerializesTraceIdAsCorrelationId()
    {
        var json = JsonSerializer.Serialize(new AnalysisListItemDto { TraceId = "trace-1" });

        json.Should().Contain("\"correlationId\":\"trace-1\"").And.NotContainEquivalentOf("traceId");
    }

    [Fact]
    public void AnalysisDetailDto_SerializesTraceIdAsCorrelationId()
    {
        var json = JsonSerializer.Serialize(new AnalysisDetailDto { TraceId = "trace-1" });

        json.Should().Contain("\"correlationId\":\"trace-1\"").And.NotContainEquivalentOf("traceId");
    }
}

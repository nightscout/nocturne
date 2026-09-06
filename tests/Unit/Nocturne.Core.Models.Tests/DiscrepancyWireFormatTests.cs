using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Nocturne.Core.Models.Tests;

public class DiscrepancyWireFormatTests
{
    [Fact]
    public void DiscrepancyAnalysisDto_SerializesTraceIdAsCorrelationId()
    {
        var json = JsonSerializer.Serialize(new DiscrepancyAnalysisDto { TraceId = "trace-1" });

        json.Should().Contain("\"correlationId\":\"trace-1\"").And.NotContainEquivalentOf("traceId");
    }
}

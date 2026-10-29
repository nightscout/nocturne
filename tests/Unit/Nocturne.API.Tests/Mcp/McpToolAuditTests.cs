using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Protocol;
using Moq;
using Nocturne.API.Mcp;
using Nocturne.API.Mcp.Tools;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Mcp;

public class McpToolAuditTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly Guid Subject = Guid.CreateVersion7();
    private static readonly Guid Grant = Guid.CreateVersion7();
    private static readonly DateTime Now = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
    private static readonly IReadOnlySet<string> Hours = new HashSet<string> { "hours" };

    private static IAuditContext Audit()
    {
        var audit = new Mock<IAuditContext>();
        audit.SetupGet(a => a.SubjectId).Returns(Subject);
        audit.SetupGet(a => a.SubjectName).Returns("Rhys");
        audit.SetupGet(a => a.AuthType).Returns("OAuthAccessToken");
        audit.SetupGet(a => a.TokenId).Returns(Grant);
        audit.SetupGet(a => a.IpAddress).Returns("203.0.113.9");
        audit.SetupGet(a => a.TraceId).Returns("trace-1");
        return audit.Object;
    }

    private static HttpContext Http()
    {
        var http = new DefaultHttpContext();
        http.Request.Headers.UserAgent = "claude-ai/1.0";
        return http;
    }

    private static CallToolRequestParams Call(string name, object arguments) => new()
    {
        Name = name,
        Arguments = JsonSerializer.SerializeToElement(arguments).EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.Clone()),
    };

    [Fact]
    public void Records_the_tool_its_arguments_and_what_it_read()
    {
        var readLog = new McpToolReadLog();
        readLog.Record("glucose", 72);
        readLog.Record("treatments", 5);
        readLog.Record("glucose", 0);

        var row = McpToolGuard.BuildAuditEntry(
            Tenant, Call("show_glucose", new { hours = 6 }), Hours, new CallToolResult(), readLog, Audit(), Http(), Now);

        row.TenantId.Should().Be(Tenant);
        row.Endpoint.Should().Be("MCP show_glucose");
        row.EntityType.Should().Be("glucose,treatments");
        row.RecordCount.Should().Be(77);
        row.QueryParametersJson.Should().Be("""{"hours":6}""");
        row.SubjectId.Should().Be(Subject);
        row.SubjectName.Should().Be("Rhys");
        row.AuthType.Should().Be("OAuthAccessToken");
        row.TokenId.Should().Be(Grant);
        row.IpAddress.Should().Be("203.0.113.9");
        row.UserAgent.Should().Be("claude-ai/1.0");
        row.TraceId.Should().Be("trace-1");
        row.StatusCode.Should().Be(200);
        row.CreatedAt.Should().Be(Now);
    }

    [Fact]
    public void Keeps_only_the_arguments_the_tool_declares()
    {
        var row = McpToolGuard.BuildAuditEntry(
            Tenant, Call("show_glucose", new { hours = 6, prompt = "whatever the model wrote" }), Hours,
            new CallToolResult(), new McpToolReadLog(), Audit(), Http(), Now);

        row.QueryParametersJson.Should().Be("""{"hours":6}""");
    }

    [Fact]
    public void A_200_kB_argument_is_filtered_and_the_row_stays_valid_json_under_the_cap()
    {
        var junk = new string('x', 200 * 1024);

        var row = McpToolGuard.BuildAuditEntry(
            Tenant, Call("show_glucose", new { hours = junk }), Hours,
            new CallToolResult(), new McpToolReadLog(), Audit(), Http(), Now);

        row.QueryParametersJson!.Length.Should().BeLessThanOrEqualTo(McpToolGuard.MaxArgumentsLength);
        JsonDocument.Parse(row.QueryParametersJson).RootElement.GetProperty("hours").GetString()
            .Should().Be(McpToolGuard.Filtered);
        McpToolGuard.MaxArgumentsLength.Should().Be(8192, "Kestrel's default request line, the MVC filter's bound");
    }

    [Fact]
    public void A_failed_call_records_500_and_no_parameters()
    {
        var failed = new CallToolResult { IsError = true };
        foreach (var result in new CallToolResult?[] { failed, null })
        {
            var row = McpToolGuard.BuildAuditEntry(
                Tenant, new CallToolRequestParams { Name = "get_current_status" }, Hours,
                result, new McpToolReadLog(), Audit(), Http(), Now);

            row.StatusCode.Should().Be(500);
            row.QueryParametersJson.Should().BeNull();
            row.EntityType.Should().BeNull();
            row.RecordCount.Should().Be(0);
        }
    }

    [Fact]
    public void Show_glucose_names_every_dataset_its_payload_carries()
    {
        var data = new DashboardChartData
        {
            GlucoseData = [new GlucosePointDto { Time = 1, Sgv = 100 }, new GlucosePointDto { Time = 2, Sgv = 110 }],
            BolusMarkers = [new BolusMarkerDto { Time = 1, Insulin = 2 }],
            DeviceEventMarkers = [new DeviceEventMarkerDto()],
            PumpModeSpans = [new ChartStateSpanDto()],
            ProfileSpans = [new ChartStateSpanDto()],
            HeartRateSeries = [new HeartRatePointDto(), new HeartRatePointDto()],
            StepSeries = [new StepBubbleDto()],
            ActivitySpans = [new ChartStateSpanDto { Kind = ChartSpanKind.Sleep }],
        };
        var log = new McpToolReadLog();

        GlucoseTools.RecordReads(data, log);

        log.Datasets.Should().Equal(
            "glucose", "treatments", "device_events", "pump_modes", "profiles", "heart_rate", "steps", "sleep");
        log.RecordCount.Should().Be(10);
        string.Join(',', log.Datasets).Length.Should().BeLessThanOrEqualTo(100, "read_access_log.entity_type is 100 long");
    }

    [Fact]
    public void The_view_domain_is_derived_from_the_tenant_mcp_url()
    {
        ReportResources.ClaudeSandboxDomain("https://rhys.nocturne.run/mcp")
            .Should().Be("ad5fdc99cefa630c1046e75d6eaf0429.claudemcpcontent.com");
    }
}

using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Protocol;
using Moq;
using Nocturne.API.Mcp;
using Nocturne.Core.Contracts.Audit;
using Xunit;

namespace Nocturne.API.Tests.Mcp;

public class McpToolAuditTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly Guid Subject = Guid.CreateVersion7();
    private static readonly Guid Grant = Guid.CreateVersion7();
    private static readonly DateTime Now = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

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

    [Fact]
    public void Records_the_tool_its_arguments_and_what_it_read()
    {
        var readLog = new McpToolReadLog();
        readLog.Record("glucose", 72);
        readLog.Record("treatments", 5);
        readLog.Record("glucose", 0);

        var call = new CallToolRequestParams
        {
            Name = "show_glucose",
            Arguments = new Dictionary<string, JsonElement> { ["hours"] = JsonSerializer.SerializeToElement(6) },
        };

        var row = McpToolGuard.BuildAuditEntry(Tenant, call, new CallToolResult(), readLog, Audit(), Http(), Now);

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
    public void A_failed_call_with_no_arguments_records_500_and_no_parameters()
    {
        var row = McpToolGuard.BuildAuditEntry(
            Tenant,
            new CallToolRequestParams { Name = "get_current_status" },
            new CallToolResult { IsError = true },
            new McpToolReadLog(),
            Audit(),
            Http(),
            Now);

        row.StatusCode.Should().Be(500);
        row.QueryParametersJson.Should().BeNull();
        row.EntityType.Should().BeNull();
        row.RecordCount.Should().Be(0);
    }

    [Fact]
    public void The_view_domain_is_derived_from_the_tenant_mcp_url()
    {
        ReportResources.ClaudeSandboxDomain("https://rhys.nocturne.run/mcp")
            .Should().Be("ad5fdc99cefa630c1046e75d6eaf0429.claudemcpcontent.com");
    }
}

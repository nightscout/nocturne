using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Nocturne.API.Mcp;
using Nocturne.API.Tests.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Mcp;

/// <summary>The tools as the running API registers them, with its services resolved.</summary>
public class McpToolSchemaTests(AuthenticationTestFactory factory) : IClassFixture<AuthenticationTestFactory>
{
    [Theory]
    [InlineData("show_glucose", new[] { "hours" })]
    [InlineData("get_current_status", new string[0])]
    [InlineData("render_report", new[] { "spec" })]
    [InlineData("describe_dataset", new[] { "name" })]
    [InlineData("refine_report", new[] { "spec", "patch" })]
    [InlineData("find_foods", new[] { "query" })]
    [InlineData("show_agp", new[] { "days" })]
    [InlineData("show_treatments", new[] { "hours" })]
    [InlineData("get_glucose_statistics", new[] { "days" })]
    [InlineData("get_recent_treatments", new[] { "hours", "types" })]
    [InlineData("get_profile_summary", new string[0])]
    [InlineData("get_loop_health", new[] { "days" })]
    [InlineData("compare_periods", new[] { "preset", "aFrom", "aTo", "bFrom", "bTo" })]
    public void The_audit_keeps_the_arguments_the_input_schema_declares(string name, string[] declared)
    {
        var tools = factory.Services.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection!;
        tools.TryGetPrimitive(name, out var tool).Should().BeTrue();

        McpToolGuard.DeclaredArguments(tool).Should().BeEquivalentTo(declared);
    }

    [Fact]
    public void Refine_report_is_for_the_view_only_and_takes_the_spec_render_report_takes()
    {
        var tools = factory.Services.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection!;
        tools.TryGetPrimitive("refine_report", out var refine).Should().BeTrue();
        tools.TryGetPrimitive("render_report", out var render).Should().BeTrue();

        var ui = refine!.ProtocolTool.Meta!["ui"]!;
        ui["visibility"]!.AsArray().Select(v => v!.GetValue<string>()).Should().Equal("app");
        ui["resourceUri"]!.GetValue<string>().Should().Be("ui://nocturne/report");
        var properties = refine.ProtocolTool.InputSchema.GetProperty("properties");
        properties.GetProperty("spec").GetProperty("properties").GetRawText()
            .Should().Be(render!.ProtocolTool.InputSchema.GetProperty("properties").GetProperty("spec").GetProperty("properties").GetRawText());
        properties.GetProperty("patch").GetProperty("properties").TryGetProperty("panels", out _).Should().BeTrue();
    }
}

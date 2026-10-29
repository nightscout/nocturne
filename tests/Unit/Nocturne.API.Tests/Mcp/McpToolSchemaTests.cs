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
    public void The_audit_keeps_the_arguments_the_input_schema_declares(string name, string[] declared)
    {
        var tools = factory.Services.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection!;
        tools.TryGetPrimitive(name, out var tool).Should().BeTrue();

        McpToolGuard.DeclaredArguments(tool).Should().BeEquivalentTo(declared);
    }
}

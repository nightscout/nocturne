using FluentAssertions;
using Nocturne.Aspire.Publishing;
using Xunit;

namespace Nocturne.Aspire.ServiceDefaults.Tests.Publishing;

public class RoutingDiagramRendererTests
{
    private static AspirePublishModel BuildModel() => new(
        Services:
        [
            new("gateway", ServiceKind.Gateway, [], [new("8080", "5000")]),
            new("nocturne-api", ServiceKind.Api, ["8080"], []),
            new("nocturne-web", ServiceKind.Web, ["8000"], []),
            new("nocturne-postgres-server", ServiceKind.Database, ["5432"], []),
            new("watchtower", ServiceKind.Container, [], []),
        ],
        Edges:
        [
            new("nocturne-web", "nocturne-api", EdgeKind.Reference),
            new("nocturne-api", "nocturne-postgres-server", EdgeKind.Reference),
            new("nocturne-web", "nocturne-postgres-server", EdgeKind.Reference),
        ],
        Routes:
        [
            new("/api/auth/oidc/{**catch-all}", "nocturne-api"),
            new("/api/{**catch-all}", "nocturne-web"),
            new(null, "nocturne-web"),
        ]
    );

    [Fact]
    public void Render_StartsWithFlowchartTd()
    {
        var result = RoutingDiagramRenderer.Render(BuildModel());
        result.Should().StartWith("flowchart TD");
    }

    [Fact]
    public void Render_GatewayAppearsInExternalSubgraph()
    {
        var result = RoutingDiagramRenderer.Render(BuildModel());
        var externalBlock = ExtractSubgraph(result, "External");
        externalBlock.Should().Contain("gateway");
    }

    [Fact]
    public void Render_InternalServicesAppearInInternalSubgraph()
    {
        var result = RoutingDiagramRenderer.Render(BuildModel());
        var internalBlock = ExtractSubgraph(result, "Internal");
        internalBlock.Should().Contain("nocturne-api");
        internalBlock.Should().Contain("nocturne-web");
        internalBlock.Should().Contain("nocturne-postgres-server");
    }

    [Fact]
    public void Render_DatabaseUsesCylinderShape()
    {
        var result = RoutingDiagramRenderer.Render(BuildModel());
        result.Should().Contain("[(");
    }

    [Fact]
    public void Render_YarpRoutesGroupedByTargetWithLabels()
    {
        var result = RoutingDiagramRenderer.Render(BuildModel());
        result.Should().Contain("nocturne-api").And.Contain("/api/auth/oidc/");
        result.Should().Contain("nocturne-web").And.Contain("/api/");
    }

    [Fact]
    public void Render_CatchAllRouteIndicatesFallback()
    {
        var result = RoutingDiagramRenderer.Render(BuildModel());
        result.Should().Contain("fallback");
    }

    [Fact]
    public void Render_ServiceEdgesPresent()
    {
        var result = RoutingDiagramRenderer.Render(BuildModel());
        result.Should().Contain("nocturne-web");
        result.Should().Contain("nocturne-api");
        result.Should().Contain("nocturne-postgres-server");
    }

    private static string ExtractSubgraph(string mermaid, string label)
    {
        var lines = mermaid.Split('\n');
        var inBlock = false;
        var depth = 0;
        var block = new System.Text.StringBuilder();
        foreach (var line in lines)
        {
            if (line.Contains("subgraph") && line.Contains(label)) { inBlock = true; depth = 1; }
            else if (inBlock && line.TrimStart().StartsWith("subgraph")) depth++;
            else if (inBlock && line.TrimStart() == "end") { depth--; if (depth == 0) break; }
            if (inBlock) block.AppendLine(line);
        }
        return block.ToString();
    }
}

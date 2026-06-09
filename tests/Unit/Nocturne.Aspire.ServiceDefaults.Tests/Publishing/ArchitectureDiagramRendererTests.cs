using FluentAssertions;
using Nocturne.Aspire.Publishing;
using Xunit;

namespace Nocturne.Aspire.ServiceDefaults.Tests.Publishing;

public class ArchitectureDiagramRendererTests
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
        Routes: []
    );

    [Fact]
    public void Render_StartsWithFlowchartLR()
    {
        var result = ArchitectureDiagramRenderer.Render(BuildModel());
        result.Should().StartWith("flowchart LR");
    }

    [Fact]
    public void Render_ContainsExternalAndInternalSubgraphs()
    {
        var result = ArchitectureDiagramRenderer.Render(BuildModel());
        result.Should().Contain("subgraph ext");
        result.Should().Contain("subgraph net");
    }

    [Fact]
    public void Render_GatewayDeclaredInExternalSubgraph()
    {
        var result = ArchitectureDiagramRenderer.Render(BuildModel());
        result.Should().MatchRegex(@"subgraph ext[^}]+gateway");
    }

    [Fact]
    public void Render_DatabaseUsesCylinderShape()
    {
        var result = ArchitectureDiagramRenderer.Render(BuildModel());
        result.Should().Contain("nocturne_postgres_server[(");
    }

    [Fact]
    public void Render_AllServicesPresent()
    {
        var result = ArchitectureDiagramRenderer.Render(BuildModel());
        result.Should().Contain("gateway");
        result.Should().Contain("nocturne_api");
        result.Should().Contain("nocturne_web");
        result.Should().Contain("nocturne_postgres_server");
        result.Should().Contain("watchtower");
    }

    [Fact]
    public void Render_EdgesPresent()
    {
        var result = ArchitectureDiagramRenderer.Render(BuildModel());
        result.Should().Contain("-->");
    }

    [Fact]
    public void Render_InternetNodePresent()
    {
        var result = ArchitectureDiagramRenderer.Render(BuildModel());
        result.Should().Contain("Internet");
    }

    [Fact]
    public void Render_WebServiceSplitsIntoBffAndFrontendSubgroup()
    {
        var result = ArchitectureDiagramRenderer.Render(BuildModel());
        result.Should().Contain("nocturne_web_group");
        result.Should().Contain("nocturne_web_bff");
        result.Should().Contain("nocturne_web_frontend");
        // Should NOT emit a plain node for the web service itself
        result.Should().NotMatchRegex(@"(?m)^\s+nocturne_web\[");
    }

    [Fact]
    public void Render_EdgesFromWebServiceOriginateFromBff()
    {
        // The test model has nocturne-web → nocturne-api; outbound side is the BFF
        var result = ArchitectureDiagramRenderer.Render(BuildModel());
        result.Should().Contain("nocturne_web_bff --> nocturne_api");
        result.Should().NotMatchRegex(@"nocturne_web -->|--> nocturne_web\b");
    }

    [Fact]
    public void Render_GatewayToWebTargetsFrontend()
    {
        var model = new AspirePublishModel(
            Services:
            [
                new("gateway", ServiceKind.Gateway, [], [new("8080", "5000")]),
                new("nocturne-web", ServiceKind.Web, ["8000"], []),
            ],
            Edges: [new("gateway", "nocturne-web", EdgeKind.Reference)],
            Routes: []
        );

        var result = ArchitectureDiagramRenderer.Render(model);
        result.Should().Contain("gateway --> nocturne_web_frontend");
        result.Should().NotContain("gateway --> nocturne_web_bff");
    }

    [Fact]
    public void Render_NonGatewayToWebTargetsBff()
    {
        // Server-to-server calls (e.g. API posting alerts to SvelteKit) target the BFF,
        // not the client bundle.
        var model = new AspirePublishModel(
            Services:
            [
                new("nocturne-api", ServiceKind.Api, ["8080"], []),
                new("nocturne-web", ServiceKind.Web, ["8000"], []),
            ],
            Edges: [new("nocturne-api", "nocturne-web", EdgeKind.Reference)],
            Routes: []
        );

        var result = ArchitectureDiagramRenderer.Render(model);
        result.Should().Contain("nocturne_api --> nocturne_web_bff");
        result.Should().NotContain("nocturne_api --> nocturne_web_frontend");
    }

    [Fact]
    public void Render_WebGroupHasFrontendToBffEdge()
    {
        var result = ArchitectureDiagramRenderer.Render(BuildModel());
        result.Should().Contain("nocturne_web_frontend --> nocturne_web_bff");
    }

    [Fact]
    public void Render_ContainerServicesAreIncluded()
    {
        // watchtower is ServiceKind.Container — it should appear as a node
        var result = ArchitectureDiagramRenderer.Render(BuildModel());
        result.Should().Contain("watchtower");
    }

    [Fact]
    public void Render_EdgesBothEndpointsMustBeDeclaredServices()
    {
        // Model with edges where one endpoint is not a declared service (simulates a
        // ParameterResource that produces an edge but no service node)
        var model = new AspirePublishModel(
            Services:
            [
                new("gateway", ServiceKind.Gateway, [], [new("8080", "5000")]),
                new("nocturne-api", ServiceKind.Api, ["8080"], []),
            ],
            Edges:
            [
                new("nocturne-api", "nocturne-postgres-server", EdgeKind.Reference), // target not in Services
                new("instance-key", "nocturne-api", EdgeKind.Reference),             // source not in Services
            ],
            Routes: []
        );

        var result = ArchitectureDiagramRenderer.Render(model);
        result.Should().NotContain("nocturne_postgres_server");
        result.Should().NotContain("instance_key");
    }
}

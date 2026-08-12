using Nocturne.API.OpenApi;
using NSwag.Generation.AspNetCore;

namespace Nocturne.API.Tests.OpenApi;

/// <summary>
/// The NSwag document feeds the published SDK specs, so its metadata is user-visible: NSwag's
/// defaults ship as "My Title" / "1.0.0" in every generated SDK unless this document sets them.
/// </summary>
public class NSwagDocumentConfigurationTests
{
    private static AspNetCoreOpenApiDocumentGeneratorSettings ConfiguredSettings()
    {
        var settings = new AspNetCoreOpenApiDocumentGeneratorSettings();
        NSwagDocumentConfiguration.Configure(settings);
        return settings;
    }

    [Fact]
    public void Document_PublishesTheNocturneTitleAndVersion()
    {
        var settings = ConfiguredSettings();

        settings.DocumentName.Should().Be("nocturne");
        settings.Title.Should().Be("Nocturne API");
        settings.Version.Should().Be("0.0.1");
    }

    [Fact]
    public void Document_HumanizesXmlSummaries()
    {
        ConfiguredSettings().OperationProcessors
            .Should().Contain(processor => processor is SummaryToDescriptionOperationProcessor);
    }
}

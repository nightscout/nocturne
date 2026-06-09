#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES004

using Aspire.Hosting;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nocturne.Aspire.Publishing;

namespace Nocturne.Aspire.Host.Publishing;

public static class MermaidDiagramPublisherExtensions
{
    /// <summary>
    /// Registers a publish pipeline step that writes Mermaid architecture diagrams
    /// (published-routing.mmd and published-services.mmd) to the publish output
    /// directory alongside the Docker Compose artifact.
    /// </summary>
    public static IDistributedApplicationBuilder AddMermaidDiagramPublisher(
        this IDistributedApplicationBuilder builder)
    {
        builder.Pipeline.AddStep(
            name: "mermaid-publish",
            action: async ctx =>
            {
                var outputService = ctx.Services.GetRequiredService<IPipelineOutputService>();
                var outputPath = outputService.GetOutputDirectory();

                Directory.CreateDirectory(outputPath);

                var publishModel = AspireModelExtractor.Extract(ctx.Model);

                var routing = RoutingDiagramRenderer.Render(publishModel);
                var architecture = ArchitectureDiagramRenderer.Render(publishModel);

                await File.WriteAllTextAsync(
                    Path.Combine(outputPath, "published-routing.mmd"),
                    routing,
                    ctx.CancellationToken);

                await File.WriteAllTextAsync(
                    Path.Combine(outputPath, "published-services.mmd"),
                    architecture,
                    ctx.CancellationToken);

                ctx.Logger.LogInformation("[mermaid-publisher] Wrote published-routing.mmd");
                ctx.Logger.LogInformation("[mermaid-publisher] Wrote published-services.mmd");
                ctx.Logger.LogInformation("[mermaid-publisher] Output: {OutputPath}", outputPath);
            },
            dependsOn: "publish-compose",
            requiredBy: WellKnownPipelineSteps.Publish);

        return builder;
    }
}

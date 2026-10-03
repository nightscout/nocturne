#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES004

using Aspire.Hosting;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Publishing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using YamlDotNet.RepresentationModel;

namespace Nocturne.Aspire.Host.Publishing;

public static class PostgresDataComposePublisherExtensions
{
    private const string PostgresDataTarget = "/var/lib/postgresql/data";

    public static IDistributedApplicationBuilder AddPostgresDataComposePublisher(
        this IDistributedApplicationBuilder builder)
    {
        builder.Pipeline.AddStep(
            name: "postgres-data-compose",
            action: async ctx =>
            {
                var outputService = ctx.Services.GetRequiredService<IPipelineOutputService>();
                var outputPath = outputService.GetOutputDirectory();
                var composePath = Path.Combine(outputPath, "docker-compose.yaml");
                var rawCompose = await File.ReadAllTextAsync(composePath, ctx.CancellationToken);
                var overrideYaml = BuildPostgresDataOverride(rawCompose);

                if (overrideYaml is null)
                {
                    ctx.Logger.LogInformation(
                        "[postgres-data-publisher] No persistent Postgres data volume in compose; skipping override");
                    return;
                }

                await File.WriteAllTextAsync(
                    Path.Combine(outputPath, "docker-compose.bind-data.yaml"),
                    overrideYaml,
                    ctx.CancellationToken);

                ctx.Logger.LogInformation(
                    "[postgres-data-publisher] Wrote docker-compose.bind-data.yaml");
            },
            dependsOn: "publish-compose",
            requiredBy: WellKnownPipelineSteps.Publish);

        return builder;
    }

    private static string? BuildPostgresDataOverride(string composeYaml)
    {
        var yaml = new YamlStream();
        using (var reader = new StringReader(composeYaml))
            yaml.Load(reader);

        var root = (YamlMappingNode)yaml.Documents[0].RootNode;
        if (root.Children["services"] is not YamlMappingNode services)
            return null;

        string? postgresServiceName = null;
        foreach (var service in services.Children)
        {
            if (service.Key is not YamlScalarNode serviceName
                || string.IsNullOrWhiteSpace(serviceName.Value)
                || service.Value is not YamlMappingNode serviceMap
                || !serviceMap.Children.TryGetValue("volumes", out var volumesNode)
                || volumesNode is not YamlSequenceNode volumes)
            {
                continue;
            }

            var hasPostgresDataVolume = volumes.Children
                .OfType<YamlMappingNode>()
                .Any(volume => volume.Children.TryGetValue("target", out var target)
                    && target is YamlScalarNode { Value: PostgresDataTarget });

            if (!hasPostgresDataVolume)
                continue;

            postgresServiceName = serviceName.Value;
            break;
        }

        if (postgresServiceName is null)
            return null;

        return string.Join('\n',
            "services:",
            $"  {postgresServiceName}:",
            "    volumes:",
            "      - type: bind",
            "        source: \"${POSTGRES_DATA_PATH:?Set POSTGRES_DATA_PATH to an existing absolute directory on the Docker host}\"",
            $"        target: \"{PostgresDataTarget}\"",
            "        read_only: false",
            "        bind:",
            "          create_host_path: false",
            "");
    }
}
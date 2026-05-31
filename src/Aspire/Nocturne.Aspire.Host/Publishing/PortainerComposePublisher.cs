#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES004

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Publishing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Nocturne.Aspire.Host.Publishing;

public static class PortainerComposePublisherExtensions
{
    /// <summary>
    /// Registers a publish pipeline step that produces a Portainer-compatible
    /// docker-compose.portainer.yaml alongside the standard docker-compose.yaml.
    /// The ./init bind-mount on the postgres service is replaced with an inline
    /// configs block so the compose is self-contained (no bind mounts required).
    /// </summary>
    public static IDistributedApplicationBuilder AddPortainerComposePublisher(
        this IDistributedApplicationBuilder builder)
    {
        var solutionRoot = Path.GetFullPath(
            Path.Combine(builder.AppHostDirectory, "..", "..", ".."));
        var initScriptPath = Path.Combine(
            solutionRoot, "docs", "postgres", "container-init", "00-init.sh");

        builder.Pipeline.AddStep(
            name: "env-metadata",
            action: async ctx =>
            {
                var outputService = ctx.Services.GetRequiredService<IPipelineOutputService>();
                var outputPath = outputService.GetOutputDirectory();

                var entries = new List<EnvVarMetadataEntry>();

                foreach (var resource in ctx.Model.Resources)
                {
                    var meta = resource.Annotations
                        .OfType<EnvVarMetadataAnnotation>()
                        .FirstOrDefault();
                    if (meta is null) continue;

                    var prefix = resource.Name.ToUpperInvariant().Replace("-", "_");

                    if (resource is ParameterResource)
                    {
                        entries.Add(new(prefix, meta.Label, meta.Description, null));
                    }
                    else
                    {
                        entries.Add(new($"{prefix}_IMAGE", meta.Label, meta.Description, meta.Default));
                        if (meta.PortLabel is not null)
                            entries.Add(new($"{prefix}_PORT", meta.PortLabel, null, null));
                    }
                }

                var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                });
                await File.WriteAllTextAsync(
                    Path.Combine(outputPath, "env-metadata.json"),
                    json,
                    ctx.CancellationToken);

                ctx.Logger.LogInformation(
                    "[env-metadata] Wrote env-metadata.json ({Count} entries)", entries.Count);
            },
            dependsOn: "publish-compose",
            requiredBy: WellKnownPipelineSteps.Publish);

        builder.Pipeline.AddStep(
            name: "portainer-compose",
            action: async ctx =>
            {
                var outputService = ctx.Services.GetRequiredService<IPipelineOutputService>();
                var outputPath = outputService.GetOutputDirectory();

                var composePath = Path.Combine(outputPath, "docker-compose.yaml");
                var rawCompose = await File.ReadAllTextAsync(composePath, ctx.CancellationToken);
                var portainerCompose = InlineInitScript(rawCompose, initScriptPath);

                await File.WriteAllTextAsync(
                    Path.Combine(outputPath, "docker-compose.portainer.yaml"),
                    portainerCompose,
                    ctx.CancellationToken);

                ctx.Logger.LogInformation("[portainer-publisher] Wrote docker-compose.portainer.yaml");
            },
            dependsOn: "publish-compose",
            requiredBy: WellKnownPipelineSteps.Publish);

        return builder;
    }

    /// <summary>
    /// Replaces the ./init bind-mount on the postgres service with a docker compose
    /// configs entry that inlines the init script. If no postgres service or no
    /// ./init bind-mount is found, returns the compose unchanged.
    /// </summary>
    private static string InlineInitScript(string composeYaml, string initScriptPath)
    {
        var yaml = new YamlStream();
        using (var reader = new StringReader(composeYaml))
            yaml.Load(reader);

        var root = (YamlMappingNode)yaml.Documents[0].RootNode;
        var services = (YamlMappingNode)root["services"];

        // Locate the postgres service — return unchanged if not present (remote DB path).
        YamlMappingNode? postgresService = null;
        foreach (var entry in services)
        {
            if (((YamlScalarNode)entry.Key).Value?.Contains(
                    "postgres", StringComparison.OrdinalIgnoreCase) == true)
            {
                postgresService = (YamlMappingNode)entry.Value;
                break;
            }
        }

        if (postgresService is null)
            return composeYaml;

        // Locate the ./init bind-mount — return unchanged if not present.
        if (!postgresService.Children.TryGetValue("volumes", out var volumesNode))
            return composeYaml;

        var volumesList = (YamlSequenceNode)volumesNode;
        YamlNode? bindMountEntry = null;
        foreach (var item in volumesList)
        {
            if (item is YamlMappingNode volumeMap
                && volumeMap.Children.TryGetValue("source", out var src)
                && ((YamlScalarNode)src).Value == "./init")
            {
                bindMountEntry = item;
                break;
            }
        }

        if (bindMountEntry is null)
            return composeYaml;

        if (!File.Exists(initScriptPath))
            throw new FileNotFoundException(
                $"[portainer-publisher] Init script not found at: {initScriptPath}", initScriptPath);

        // Docker Compose interpolates `configs.*.content` when loading the file,
        // so the script's runtime shell references (e.g. ${POSTGRES_DB:?...}) must
        // be escaped to `$$` to survive parse-time and reach the container intact.
        var initScriptContent = File.ReadAllText(initScriptPath).Replace("$", "$$");

        // Remove the ./init bind-mount.
        volumesList.Children.Remove(bindMountEntry);

        // Add configs reference to the postgres service.
        postgresService.Children[new YamlScalarNode("configs")] = new YamlSequenceNode(
            new YamlMappingNode(
                new YamlScalarNode("source"), new YamlScalarNode("nocturne-init"),
                new YamlScalarNode("target"),
                    new YamlScalarNode("/docker-entrypoint-initdb.d/00-init.sh"),
                new YamlScalarNode("mode"),
                    new YamlScalarNode("493") { Style = ScalarStyle.Plain }
            )
        );

        // Add top-level configs key with inlined script content.
        var configContent = new YamlMappingNode();
        configContent.Children[new YamlScalarNode("content")] =
            new YamlScalarNode(initScriptContent) { Style = ScalarStyle.Literal };

        var topLevelConfigs = new YamlMappingNode();
        topLevelConfigs.Children[new YamlScalarNode("nocturne-init")] = configContent;
        root.Children[new YamlScalarNode("configs")] = topLevelConfigs;

        var sb = new StringBuilder();
        using (var writer = new StringWriter(sb))
            yaml.Save(writer, assignAnchors: false);

        return sb.ToString();
    }
}

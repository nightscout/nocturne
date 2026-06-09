#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES004

using System.Linq;
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
    /// The ./init and ./caddy/Caddyfile bind-mounts are replaced with inline configs
    /// blocks so both bundles are self-contained (just compose + .env, no bind mounts).
    /// </summary>
    public static IDistributedApplicationBuilder AddPortainerComposePublisher(
        this IDistributedApplicationBuilder builder)
    {
        var solutionRoot = Path.GetFullPath(
            Path.Combine(builder.AppHostDirectory, "..", "..", ".."));
        var initScriptPath = Path.Combine(
            solutionRoot, "docs", "postgres", "container-init", "00-init.sh");
        var caddyDirectoryPath = Path.Combine(builder.AppHostDirectory, "caddy");
        var caddyfilePath = Path.Combine(caddyDirectoryPath, "Caddyfile");

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
                        entries.Add(new(prefix, meta.Label, meta.Description, meta.Default));
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
                var portainerCompose = SelfContainCompose(rawCompose, initScriptPath, caddyfilePath);

                await File.WriteAllTextAsync(
                    Path.Combine(outputPath, "docker-compose.portainer.yaml"),
                    portainerCompose,
                    ctx.CancellationToken);

                ctx.Logger.LogInformation("[portainer-publisher] Wrote docker-compose.portainer.yaml");
            },
            dependsOn: "publish-compose",
            requiredBy: WellKnownPipelineSteps.Publish);

        // Harden the standard docker-compose.yaml: (1) inline the init script and the
        // Caddyfile as Compose configs instead of ./init and ./caddy/Caddyfile bind-mounts,
        // so the release only needs to ship docker-compose.yaml + .env (no separate
        // directories); and (2) give Postgres a real healthcheck and gate the API's
        // dependency on service_healthy, so `docker compose up` waits for the database
        // to be ready before the API runs migrations. Idempotent.
        builder.Pipeline.AddStep(
            name: "harden-main-compose",
            action: async ctx =>
            {
                var outputService = ctx.Services.GetRequiredService<IPipelineOutputService>();
                var outputPath = outputService.GetOutputDirectory();

                var composePath = Path.Combine(outputPath, "docker-compose.yaml");
                var rawCompose = await File.ReadAllTextAsync(composePath, ctx.CancellationToken);
                var transformed = SelfContainCompose(rawCompose, initScriptPath, caddyfilePath);

                if (transformed != rawCompose)
                {
                    await File.WriteAllTextAsync(composePath, transformed, ctx.CancellationToken);
                    ctx.Logger.LogInformation(
                        "[compose] Inlined init script + Caddyfile, added Postgres healthcheck/service_healthy gate");
                }
            },
            dependsOn: "publish-compose",
            requiredBy: WellKnownPipelineSteps.Publish);

        return builder;
    }

    /// <summary>
    /// Produces a self-contained compose: inlines the init script and the Caddyfile
    /// as Compose configs (removing the ./init and ./caddy/Caddyfile bind-mounts) and
    /// adds the Postgres readiness healthcheck + service_healthy gate. Each transform
    /// is idempotent and no-ops when its target isn't present, so applying it to an
    /// already-transformed compose is safe.
    /// </summary>
    private static string SelfContainCompose(string composeYaml, string initScriptPath, string caddyfilePath)
        => InlineCaddyfile(
            HardenPostgresStartup(InlineInitScript(composeYaml, initScriptPath)),
            caddyfilePath);

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

    /// <summary>
    /// Replaces the ./caddy/Caddyfile bind-mount on the caddy service with a docker
    /// compose configs entry that inlines the Caddyfile, so the portainer compose
    /// is self-contained. If no caddy service or no Caddyfile bind-mount is found
    /// (e.g. Caddy disabled), returns the compose unchanged.
    /// </summary>
    private static string InlineCaddyfile(string composeYaml, string caddyfilePath)
    {
        var yaml = new YamlStream();
        using (var reader = new StringReader(composeYaml))
            yaml.Load(reader);

        var root = (YamlMappingNode)yaml.Documents[0].RootNode;
        var services = (YamlMappingNode)root["services"];

        // Locate the caddy service — return unchanged if not present (Caddy disabled).
        var caddyEntry = services.FirstOrDefault(entry =>
            entry.Key is YamlScalarNode key
            && key.Value?.Contains("caddy", StringComparison.OrdinalIgnoreCase) == true);
        YamlMappingNode? caddyService = caddyEntry.Value as YamlMappingNode;

        if (caddyService is null)
            return composeYaml;

        // Locate the ./caddy/Caddyfile bind-mount — return unchanged if not present.
        if (!caddyService.Children.TryGetValue("volumes", out var volumesNode))
            return composeYaml;

        var volumesList = (YamlSequenceNode)volumesNode;
        var bindMountEntry = volumesList
            .Where(item => item is YamlMappingNode volumeMap
                && volumeMap.Children.TryGetValue("source", out var src)
                && ((YamlScalarNode)src).Value == "./caddy/Caddyfile")
            .FirstOrDefault();

        if (bindMountEntry is null)
            return composeYaml;

        if (!File.Exists(caddyfilePath))
            throw new FileNotFoundException(
                $"[portainer-publisher] Caddyfile not found at: {caddyfilePath}", caddyfilePath);

        // Compose interpolates configs.*.content at load time, so the Caddyfile's
        // {$BASE_DOMAIN} env reference must be escaped to $$ to survive parse-time
        // and reach Caddy intact (it expands the var from its container environment).
        var caddyfileContent = File.ReadAllText(caddyfilePath).Replace("$", "$$");

        // Remove the ./caddy/Caddyfile bind-mount (leaving the caddy-data /
        // caddy-config named volumes intact).
        volumesList.Children.Remove(bindMountEntry);

        // Add configs reference to the caddy service.
        caddyService.Children[new YamlScalarNode("configs")] = new YamlSequenceNode(
            new YamlMappingNode(
                new YamlScalarNode("source"), new YamlScalarNode("nocturne-caddyfile"),
                new YamlScalarNode("target"), new YamlScalarNode("/etc/caddy/Caddyfile"),
                new YamlScalarNode("mode"),
                    new YamlScalarNode("420") { Style = ScalarStyle.Plain }
            )
        );

        // Append to the top-level configs key (the init script may have already
        // created it), or create it if this is the first inlined config.
        var configContent = new YamlMappingNode();
        configContent.Children[new YamlScalarNode("content")] =
            new YamlScalarNode(caddyfileContent) { Style = ScalarStyle.Literal };

        YamlMappingNode topLevelConfigs;
        if (root.Children.TryGetValue("configs", out var existingConfigs)
            && existingConfigs is YamlMappingNode existingMap)
        {
            topLevelConfigs = existingMap;
        }
        else
        {
            topLevelConfigs = new YamlMappingNode();
            root.Children[new YamlScalarNode("configs")] = topLevelConfigs;
        }
        topLevelConfigs.Children[new YamlScalarNode("nocturne-caddyfile")] = configContent;

        var sb = new StringBuilder();
        using (var writer = new StringWriter(sb))
            yaml.Save(writer, assignAnchors: false);

        return sb.ToString();
    }

    /// <summary>
    /// Adds a readiness healthcheck to the postgres service and flips any
    /// dependent's <c>depends_on</c> condition from <c>service_started</c> to
    /// <c>service_healthy</c>, so `docker compose up` waits for the database to
    /// accept connections before starting the API (which migrates on boot).
    ///
    /// The healthcheck targets the real TCP listener (127.0.0.1:5432). The
    /// postgres image's first-boot init runs a temporary socket-only server, so
    /// a TCP probe correctly reports "not ready" during init and only goes
    /// healthy once the real server is up — avoiding a premature healthy status.
    /// Returns the compose unchanged if no postgres service is present.
    /// </summary>
    private static string HardenPostgresStartup(string composeYaml)
    {
        var yaml = new YamlStream();
        using (var reader = new StringReader(composeYaml))
            yaml.Load(reader);

        var root = (YamlMappingNode)yaml.Documents[0].RootNode;
        var services = (YamlMappingNode)root["services"];

        string? postgresName = null;
        YamlMappingNode? postgresService = null;
        foreach (var entry in services)
        {
            var name = ((YamlScalarNode)entry.Key).Value;
            if (name?.Contains("postgres", StringComparison.OrdinalIgnoreCase) == true)
            {
                postgresName = name;
                postgresService = (YamlMappingNode)entry.Value;
                break;
            }
        }

        if (postgresService is null || postgresName is null)
            return composeYaml;

        // Add the healthcheck (skip if one already exists).
        if (!postgresService.Children.ContainsKey(new YamlScalarNode("healthcheck")))
        {
            var healthcheck = new YamlMappingNode
            {
                {
                    new YamlScalarNode("test"),
                    new YamlSequenceNode(
                        new YamlScalarNode("CMD-SHELL"),
                        new YamlScalarNode("pg_isready -h 127.0.0.1 -p 5432"))
                },
                { new YamlScalarNode("interval"), new YamlScalarNode("5s") },
                { new YamlScalarNode("timeout"), new YamlScalarNode("5s") },
                { new YamlScalarNode("retries"), new YamlScalarNode("12") { Style = ScalarStyle.Plain } },
                { new YamlScalarNode("start_period"), new YamlScalarNode("60s") },
            };
            postgresService.Children[new YamlScalarNode("healthcheck")] = healthcheck;
        }

        // Flip every service that waits on postgres to require it to be healthy.
        foreach (var entry in services)
        {
            if (entry.Value is YamlMappingNode svc
                && svc.Children.TryGetValue(new YamlScalarNode("depends_on"), out var dep)
                && dep is YamlMappingNode depMap
                && depMap.Children.TryGetValue(new YamlScalarNode(postgresName), out var cond)
                && cond is YamlMappingNode condMap)
            {
                condMap.Children[new YamlScalarNode("condition")] =
                    new YamlScalarNode("service_healthy");
            }
        }

        var sb = new StringBuilder();
        using (var writer = new StringWriter(sb))
            yaml.Save(writer, assignAnchors: false);

        return sb.ToString();
    }
}

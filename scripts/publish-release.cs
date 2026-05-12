// scripts/publish-release.cs
//
// Generates the production Docker Compose bundle for GitHub Releases.
//
// Usage:
//   dotnet run scripts/publish-release.cs [output-dir]
//
// Requires: .NET 10 SDK, Aspire CLI

#:package YamlDotNet

using System.Diagnostics;
using System.Text;
using YamlDotNet.RepresentationModel;

var repoRoot = Directory.GetCurrentDirectory();
var outputDir = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(repoRoot, "release-output");
var appHostDir = Path.Combine(repoRoot, "src", "Aspire", "Nocturne.Aspire.Host");
var tempDir = Path.Combine(Path.GetTempPath(), $"nocturne-release-{Guid.NewGuid():N}");

Directory.CreateDirectory(outputDir);
Directory.CreateDirectory(tempDir);

try
{
    Console.WriteLine("[publish-release] Generating production docker-compose...");

    // Run aspire publish with production flags
    var aspireEnv = new Dictionary<string, string>
    {
        ["Aspire__OptionalServices__AspireDashboard__Enabled"] = "false",
        ["Aspire__OptionalServices__Scalar__Enabled"] = "false",
        ["Aspire__OptionalServices__Watchtower__Enabled"] = "true",
    };

    var exitCode = RunProcess("aspire", [
        "publish",
        "--project", appHostDir,
        "--publisher", "docker-compose",
        "--output-path", tempDir,
        "--no-build",
        "--non-interactive"
    ], aspireEnv);

    if (exitCode != 0)
    {
        Console.Error.WriteLine("[publish-release] ERROR: aspire publish failed");
        return 1;
    }

    var composePath = Path.Combine(tempDir, "docker-compose.yaml");
    if (!File.Exists(composePath))
    {
        Console.Error.WriteLine("[publish-release] ERROR: aspire publish did not produce docker-compose.yaml");
        return 1;
    }

    // Inline the init script via docker compose configs so the compose is
    // self-contained — no bind mounts, compatible with Portainer CE.
    var initScriptSource = Path.Combine(repoRoot, "docs", "postgres", "container-init", "00-init.sh");
    var composeYaml = File.ReadAllText(composePath);
    var processedCompose = InlineInitScript(composeYaml, initScriptSource);

    var composeOutputPath = Path.Combine(outputDir, "docker-compose.yaml");
    File.WriteAllText(composeOutputPath, processedCompose);
    Console.WriteLine($"[publish-release] Wrote {composeOutputPath}");

    // Generate .env.example from aspire-generated .env
    var aspireEnvPath = Path.Combine(tempDir, ".env");
    var envExampleOutputPath = Path.Combine(outputDir, ".env.example");
    GenerateEnvExample(aspireEnvPath, envExampleOutputPath);
    Console.WriteLine($"[publish-release] Wrote {envExampleOutputPath}");

    // Write processed compose and .env.example to deploy/portainer/ in the repo
    // so they can be committed and used directly from the repository.
    var deployPortainerDir = Path.Combine(repoRoot, "deploy", "portainer");
    Directory.CreateDirectory(deployPortainerDir);
    File.WriteAllText(Path.Combine(deployPortainerDir, "docker-compose.yaml"), processedCompose);
    File.Copy(envExampleOutputPath, Path.Combine(deployPortainerDir, ".env.example"), overwrite: true);
    Console.WriteLine($"[publish-release] Updated deploy/portainer/ (commit these before tagging)");

    Console.WriteLine();
    Console.WriteLine("[publish-release] Done! Output files:");
    Console.WriteLine($"  {composeOutputPath}");
    Console.WriteLine($"  {envExampleOutputPath}");

    return 0;
}
finally
{
    if (Directory.Exists(tempDir))
        Directory.Delete(tempDir, recursive: true);
}

// Replaces the ./init bind-mount on nocturne-postgres-server with a docker
// compose configs entry that inlines the init script content directly.
// This makes the compose self-contained and compatible with Portainer CE.
static string InlineInitScript(string composeYaml, string initScriptPath)
{
    if (!File.Exists(initScriptPath))
        throw new FileNotFoundException(
            $"[publish-release] Init script not found at: {initScriptPath}", initScriptPath);

    var initScriptContent = File.ReadAllText(initScriptPath);

    var yaml = new YamlStream();
    using (var reader = new StringReader(composeYaml))
        yaml.Load(reader);

    var root = (YamlMappingNode)yaml.Documents[0].RootNode;

    // Locate the postgres service
    var services = (YamlMappingNode)root["services"];
    YamlMappingNode? postgresService = null;
    foreach (var entry in services)
    {
        var key = ((YamlScalarNode)entry.Key).Value ?? "";
        if (key.Contains("postgres", StringComparison.OrdinalIgnoreCase))
        {
            postgresService = (YamlMappingNode)entry.Value;
            break;
        }
    }

    if (postgresService is null)
        throw new InvalidOperationException("Could not find postgres service in docker-compose.yaml");

    // Remove the ./init bind-mount from the postgres service volumes
    if (postgresService.Children.TryGetValue("volumes", out var volumesNode))
    {
        var volumesList = (YamlSequenceNode)volumesNode;
        YamlNode? bindMountEntry = null;
        foreach (var item in volumesList)
        {
            if (item is YamlMappingNode volumeMap
                && volumeMap.Children.TryGetValue("source", out var sourceNode)
                && ((YamlScalarNode)sourceNode).Value == "./init")
            {
                bindMountEntry = item;
                break;
            }
        }
        if (bindMountEntry is null)
            throw new InvalidOperationException(
                "[publish-release] Could not find './init' bind-mount in postgres service volumes. " +
                "Has the Aspire output format changed?");
        volumesList.Children.Remove(bindMountEntry);
    }

    // Add configs reference to the postgres service
    var configsRef = new YamlSequenceNode(
        new YamlMappingNode(
            new YamlScalarNode("source"), new YamlScalarNode("nocturne-init"),
            new YamlScalarNode("target"), new YamlScalarNode("/docker-entrypoint-initdb.d/00-init.sh"),
            new YamlScalarNode("mode"), new YamlScalarNode("493") { Style = YamlDotNet.Core.ScalarStyle.Plain }
        )
    );
    postgresService.Children[new YamlScalarNode("configs")] = configsRef;

    // Add top-level configs key with inlined script content
    var configContent = new YamlMappingNode();
    configContent.Children[new YamlScalarNode("content")] = new YamlScalarNode(initScriptContent)
    {
        Style = YamlDotNet.Core.ScalarStyle.Literal,
    };
    var topLevelConfigs = new YamlMappingNode();
    topLevelConfigs.Children[new YamlScalarNode("nocturne-init")] = configContent;
    root.Children[new YamlScalarNode("configs")] = topLevelConfigs;

    // Serialize back to YAML
    var sb = new StringBuilder();
    using (var writer = new StringWriter(sb))
        yaml.Save(writer, assignAnchors: false);

    return sb.ToString();
}

static void GenerateEnvExample(
    string aspireEnvPath,
    string outputPath)
{
    // Known defaults for non-secret values
    var defaults = new Dictionary<string, string>
    {
        ["NOCTURNE_API_IMAGE"] = "ghcr.io/nightscout/nocturne/nocturne-api:latest",
        ["NOCTURNE_WEB_IMAGE"] = "ghcr.io/nightscout/nocturne/nocturne-web:latest",
        ["NOCTURNE_API_PORT"] = "8080",
        ["POSTGRES_USERNAME"] = "nocturne",
    };

    // Secret vars -- leave blank
    var secrets = new HashSet<string>
    {
        "POSTGRES_PASSWORD",
        "POSTGRES_MIGRATOR_PASSWORD",
        "POSTGRES_APP_PASSWORD",
        "POSTGRES_WEB_PASSWORD",
        "INSTANCE_KEY",
    };

    // Required config (not secrets, but must be set)
    var requiredConfig = new HashSet<string>
    {
        "BASE_DOMAIN",
    };

    // Optional vars (all bot integration config — leave blank if not using)
    var optional = new HashSet<string>
    {
        "DISCORD_BOT_TOKEN",
        "DISCORD_APPLICATION_ID",
        "DISCORD_CLIENT_SECRET",
        "DISCORD_PUBLIC_KEY",
        "TELEGRAM_BOT_TOKEN",
        "TELEGRAM_WEBHOOK_SECRET_TOKEN",
        "SLACK_BOT_TOKEN",
        "SLACK_SIGNING_SECRET",
        "WHATSAPP_ACCESS_TOKEN",
        "WHATSAPP_APP_SECRET",
        "WHATSAPP_PHONE_NUMBER_ID",
        "WHATSAPP_VERIFY_TOKEN",
    };

    using var writer = new StreamWriter(outputPath);
    writer.WriteLine("# Nocturne Production Environment");
    writer.WriteLine("# See: https://github.com/nightscout/nocturne/releases");
    writer.WriteLine("#");
    writer.WriteLine("# Copy this file to .env and fill in the required values.");
    writer.WriteLine("# Passwords are only used on first database initialization.");
    writer.WriteLine();

    if (File.Exists(aspireEnvPath))
    {
        var seenVars = new HashSet<string>();
        var configVars = new List<(string name, string value)>();
        var requiredConfigVars = new List<(string name, string value)>();
        var secretVars = new List<(string name, string value)>();
        var optionalVars = new List<(string name, string value)>();

        foreach (var line in File.ReadLines(aspireEnvPath))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                continue;

            var eqIndex = line.IndexOf('=');
            if (eqIndex < 0) continue;

            var name = line[..eqIndex];

            if (!seenVars.Add(name)) continue;

            // Skip Aspire internal bind-mount placeholder vars — they're meaningless
            // to users after the init script has been inlined via configs.content.
            if (name.Contains("_BINDMOUNT_", StringComparison.OrdinalIgnoreCase)) continue;

            if (secrets.Contains(name))
                secretVars.Add((name, ""));
            else if (requiredConfig.Contains(name))
                requiredConfigVars.Add((name, ""));
            else if (optional.Contains(name))
                optionalVars.Add((name, defaults.GetValueOrDefault(name, "")));
            else
                configVars.Add((name, defaults.GetValueOrDefault(name, "")));
        }

        writer.WriteLine("# -- Configuration ---------------------------------------------");
        writer.WriteLine();
        foreach (var (name, value) in configVars)
            writer.WriteLine($"{name}={value}");

        writer.WriteLine();
        writer.WriteLine("# -- Required (set these before first run) ----------------------");
        writer.WriteLine();
        foreach (var (name, _) in requiredConfigVars)
            writer.WriteLine($"{name}=");
        foreach (var (name, _) in secretVars)
            writer.WriteLine($"{name}=");

        writer.WriteLine();
        writer.WriteLine("# -- Optional --------------------------------------------------");
        writer.WriteLine();
        foreach (var (name, value) in optionalVars)
            writer.WriteLine($"# {name}=");
    }
    else
    {
        // Fallback if aspire didn't generate .env
        writer.WriteLine("NOCTURNE_API_IMAGE=ghcr.io/nightscout/nocturne/nocturne-api:latest");
        writer.WriteLine("NOCTURNE_WEB_IMAGE=ghcr.io/nightscout/nocturne/nocturne-web:latest");
        writer.WriteLine("NOCTURNE_API_PORT=8080");
        writer.WriteLine("POSTGRES_USERNAME=nocturne");
        writer.WriteLine();
        writer.WriteLine("BASE_DOMAIN=");
        writer.WriteLine("POSTGRES_PASSWORD=");
        writer.WriteLine("POSTGRES_MIGRATOR_PASSWORD=");
        writer.WriteLine("POSTGRES_APP_PASSWORD=");
        writer.WriteLine("POSTGRES_WEB_PASSWORD=");
        writer.WriteLine("INSTANCE_KEY=");
    }
}

static int RunProcess(string command, string[] arguments, Dictionary<string, string>? env = null)
{
    var psi = new ProcessStartInfo(command)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };

    foreach (var arg in arguments)
        psi.ArgumentList.Add(arg);

    if (env is not null)
    {
        foreach (var (key, value) in env)
            psi.Environment[key] = value;
    }

    using var process = Process.Start(psi)!;
    var stdoutTask = process.StandardOutput.ReadToEndAsync();
    var stderrTask = process.StandardError.ReadToEndAsync();
    process.WaitForExit();

    var stdout = stdoutTask.Result;
    var stderr = stderrTask.Result;

    if (!string.IsNullOrWhiteSpace(stdout)) Console.Write(stdout);
    if (!string.IsNullOrWhiteSpace(stderr)) Console.Error.Write(stderr);

    return process.ExitCode;
}

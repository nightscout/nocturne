// scripts/publish-release.cs
//
// Generates the production Docker Compose bundle into deploy/ and docs/diagrams/. The
// compose-bundle-drift job in .github/workflows/tests.yml regenerates it and fails a pull
// request whose committed copy differs.
//
// Usage:
//   dotnet run scripts/publish-release.cs
//
// Requires: .NET 10 SDK, Aspire CLI

#:project Shared/Shared.csproj

#pragma warning disable IL2026, IL3050 // Script — never trimmed or AOT-compiled.

using System.Text.Json;
using System.Text.Json.Nodes;
using static ProcessHelpers;

var repoRoot = Directory.GetCurrentDirectory();
var appHostDir = Path.Combine(repoRoot, "src", "Aspire", "Nocturne.Aspire.Host");
var tempDir = Path.Combine(Path.GetTempPath(), $"nocturne-release-{Guid.NewGuid():N}");

Directory.CreateDirectory(tempDir);

try
{
    Console.WriteLine("[publish-release] Generating production docker-compose...");

    var aspireEnv = new Dictionary<string, string>
    {
        ["Aspire__OptionalServices__AspireDashboard__Enabled"] = "false",
        ["Aspire__OptionalServices__Scalar__Enabled"] = "false",
        ["Aspire__OptionalServices__Watchtower__Enabled"] = "true",
        // The AppHost defaults to ephemeral Postgres in a git worktree, which drops the
        // named data volume — never right for a release bundle.
        ["NOCTURNE_DB_PERSISTENCE"] = "persistent",
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
    var portainerComposePath = Path.Combine(tempDir, "docker-compose.portainer.yaml");
    var byoProxyComposePath = Path.Combine(tempDir, "docker-compose.byo-proxy.yaml");
    var postgresDataComposePath = Path.Combine(tempDir, "docker-compose.bind-data.yaml");
    var envMetadataPath = Path.Combine(tempDir, "env-metadata.json");

    if (!File.Exists(composePath))
    {
        Console.Error.WriteLine("[publish-release] ERROR: aspire publish did not produce docker-compose.yaml");
        return 1;
    }

    if (!File.Exists(portainerComposePath))
    {
        Console.Error.WriteLine("[publish-release] ERROR: aspire publish did not produce docker-compose.portainer.yaml");
        return 1;
    }

    if (!File.Exists(envMetadataPath))
    {
        Console.Error.WriteLine("[publish-release] ERROR: aspire publish did not produce env-metadata.json");
        return 1;
    }

    if (!File.Exists(postgresDataComposePath))
    {
        Console.Error.WriteLine("[publish-release] ERROR: aspire publish did not produce docker-compose.bind-data.yaml");
        return 1;
    }

    // Parse via JsonNode rather than reflection-based Deserialize: file-based
    // apps run with reflection serialization disabled, which would throw.
    var envMetadata = JsonNode.Parse(File.ReadAllText(envMetadataPath))!
        .AsArray()
        .Select(n => new EnvVarMeta(
            (string)n!["name"]!,
            (string)n["label"]!,
            (string?)n["description"],
            (string?)n["default"]))
        .ToArray();

    var groups = ParseAspireEnv(Path.Combine(tempDir, ".env"), envMetadata);
    var envExample = GenerateEnvExample(groups, envMetadata);

    // deploy/docker-compose/ — self-contained aspire output. The init script and
    // the Caddyfile are inlined into the compose as configs (see
    // PortainerComposePublisher), so no separate init/ or caddy/ directories are
    // shipped; the bundle is just compose + .env.
    var deployDockerComposeDir = Path.Combine(repoRoot, "deploy", "docker-compose");
    var dockerComposeWritten = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    Directory.CreateDirectory(deployDockerComposeDir);
    File.Copy(composePath, Owned(deployDockerComposeDir, "docker-compose.yaml", dockerComposeWritten), overwrite: true);
    // Bring-your-own-proxy override (generated alongside the compose by the
    // byo-proxy publish step; absent when Caddy is disabled).
    if (File.Exists(byoProxyComposePath))
        File.Copy(byoProxyComposePath, Owned(deployDockerComposeDir, "docker-compose.byo-proxy.yaml", dockerComposeWritten), overwrite: true);
    File.Copy(postgresDataComposePath, Owned(deployDockerComposeDir, "docker-compose.bind-data.yaml", dockerComposeWritten), overwrite: true);
    File.WriteAllText(Owned(deployDockerComposeDir, ".env.example", dockerComposeWritten), envExample);
    PruneUnwritten(repoRoot, deployDockerComposeDir, dockerComposeWritten);
    Console.WriteLine("[publish-release] Updated deploy/docker-compose/");

    // deploy/portainer/ — self-contained compose with inlined init script.
    var deployPortainerDir = Path.Combine(repoRoot, "deploy", "portainer");
    var portainerWritten = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    Directory.CreateDirectory(deployPortainerDir);
    File.Copy(portainerComposePath, Owned(deployPortainerDir, "docker-compose.yaml", portainerWritten), overwrite: true);
    File.WriteAllText(Owned(deployPortainerDir, ".env.example", portainerWritten), envExample);
    File.WriteAllText(Owned(deployPortainerDir, "templates.json", portainerWritten), GeneratePortainerTemplate(groups, envMetadata));
    PruneUnwritten(repoRoot, deployPortainerDir, portainerWritten);
    Console.WriteLine("[publish-release] Updated deploy/portainer/");

    // docs/diagrams/ — Mermaid architecture diagrams produced by MermaidDiagramPublisher.
    var diagramsDir = Path.Combine(repoRoot, "docs", "diagrams");
    Directory.CreateDirectory(diagramsDir);
    foreach (var mmd in Directory.GetFiles(tempDir, "*.mmd"))
        File.Copy(mmd, Path.Combine(diagramsDir, Path.GetFileName(mmd)), overwrite: true);
    Console.WriteLine("[publish-release] Updated docs/diagrams/");

    Console.WriteLine();
    Console.WriteLine("[publish-release] Done! Commit deploy/ and docs/diagrams/. The compose-bundle-drift job checks them.");

    return 0;
}
finally
{
    if (Directory.Exists(tempDir))
        Directory.Delete(tempDir, recursive: true);
}

// ── output ownership ──────────────────────────────────────────────────────────

static string Owned(string dir, string name, HashSet<string> written)
{
    var path = Path.Combine(dir, name);
    written.Add(path);
    return path;
}

static void PruneUnwritten(string repoRoot, string dir, HashSet<string> written)
{
    foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
    {
        if (written.Contains(file))
            continue;
        File.Delete(file);
        Console.WriteLine($"[publish-release] Deleted {Path.GetRelativePath(repoRoot, file).Replace('\\', '/')}");
    }
}

// ── env parsing ───────────────────────────────────────────────────────────────

static EnvVarGroups ParseAspireEnv(string aspireEnvPath, EnvVarMeta[] metadata)
{
    var secrets = new HashSet<string>
    {
        "POSTGRES_PASSWORD",
        "POSTGRES_MIGRATOR_PASSWORD",
        "POSTGRES_APP_PASSWORD",
        "POSTGRES_WEB_PASSWORD",
        "INSTANCE_KEY",
    };

    var requiredConfig = new HashSet<string>
    {
        "BASE_DOMAIN",
    };

    var optional = new HashSet<string>
    {
        "DISCORD_BOT_TOKEN",
        "DISCORD_APPLICATION_ID",
        "DISCORD_CLIENT_SECRET",
        "DISCORD_PUBLIC_KEY",
        "GOOGLE_HEALTH_CLIENT_ID",
        "GOOGLE_HEALTH_CLIENT_SECRET",
        "GOOGLE_HEALTH_REDIRECT_URI",
        "TELEGRAM_BOT_TOKEN",
        "TELEGRAM_WEBHOOK_SECRET_TOKEN",
        "SLACK_BOT_TOKEN",
        "SLACK_SIGNING_SECRET",
        "WHATSAPP_ACCESS_TOKEN",
        "WHATSAPP_APP_SECRET",
        "WHATSAPP_PHONE_NUMBER_ID",
        "WHATSAPP_VERIFY_TOKEN",
        "RESEND_API_KEY",
        "RESEND_FROM_ADDRESS",
        "RESEND_FROM_NAME",
        "RESEND_WEBHOOK_SECRET",
        "WATCHTOWER_API_TOKEN",
    };

    var seen = new HashSet<string>();
    var configVars = new List<(string, string)>();
    var requiredConfigVars = new List<(string, string)>();
    var secretVars = new List<(string, string)>();
    var optionalVars = new List<(string, string)>();

    if (File.Exists(aspireEnvPath))
    {
        foreach (var line in File.ReadLines(aspireEnvPath))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                continue;

            var eqIndex = line.IndexOf('=');
            if (eqIndex < 0) continue;

            var name = line[..eqIndex];
            var value = line[(eqIndex + 1)..];

            if (!seen.Add(name)) continue;
            if (name.Contains("_BINDMOUNT_", StringComparison.OrdinalIgnoreCase)) continue;

            if (secrets.Contains(name))
                secretVars.Add((name, ""));
            else if (requiredConfig.Contains(name))
                requiredConfigVars.Add((name, ""));
            else if (optional.Contains(name))
                optionalVars.Add((name, value));
            else
                configVars.Add((name, value));
        }
    }

    // Fill in defaults from env-metadata for config vars that Aspire emits as empty.
    var metaDefaults = metadata
        .Where(m => m.Default is not null)
        .ToDictionary(m => m.Name, m => m.Default!);
    configVars = [.. configVars
        .Select(v => string.IsNullOrEmpty(v.Item2) && metaDefaults.TryGetValue(v.Item1, out var def)
            ? (v.Item1, def)
            : v)];

    return new EnvVarGroups(configVars, requiredConfigVars, secretVars, optionalVars);
}

// ── .env.example ──────────────────────────────────────────────────────────────

static string GenerateEnvExample(EnvVarGroups groups, EnvVarMeta[] metadata)
{
    var varComments = metadata
        .Where(m => m.Description is not null)
        .ToDictionary(m => m.Name, m => $"# {m.Description}");

    var sb = new System.Text.StringBuilder();

    void AppendVar(string name, string value)
    {
        if (varComments.TryGetValue(name, out var comment))
            sb.AppendLine(comment);
        sb.AppendLine($"{name}={value}");
    }

    sb.AppendLine("# Nocturne Production Environment");
    sb.AppendLine("# See: https://github.com/nightscout/nocturne/releases");
    sb.AppendLine("#");
    sb.AppendLine("# Copy this file to .env and fill in the required values. The install guide can make");
    sb.AppendLine("# the file for you: https://getnocturne.dev/docs/installation/docker-compose?utm_source=docker-compose&utm_medium=config");
    sb.AppendLine("#");
    sb.AppendLine("# Keep a copy of this file somewhere other than this server. If you lose INSTANCE_KEY,");
    sb.AppendLine("# Nocturne cannot read the connector passwords and other secrets that it stores.");
    sb.AppendLine("# The database takes its passwords from this file at its first start. Changing them");
    sb.AppendLine("# here later does not change them in the database.");
    sb.AppendLine();
    sb.AppendLine("# -- Required (set these before first run) ----------------------");
    sb.AppendLine();
    foreach (var (name, _) in groups.RequiredConfig)
        AppendVar(name, "");
    foreach (var (name, _) in groups.Secrets)
        AppendVar(name, "");
    sb.AppendLine();
    sb.AppendLine("# -- Configuration (defaults work; change if you need to) --------");
    sb.AppendLine();
    foreach (var (name, value) in groups.Config)
        AppendVar(name, value);
    sb.AppendLine();
    sb.AppendLine("# -- Optional --------------------------------------------------");
    sb.AppendLine();
    foreach (var (name, _) in groups.Optional)
        sb.AppendLine($"# {name}=");

    return sb.ToString();
}

// ── portainer templates.json ──────────────────────────────────────────────────

static string GeneratePortainerTemplate(EnvVarGroups groups, EnvVarMeta[] metadata)
{
    // Config vars have defaults applied by ParseAspireEnv; secrets stay blank.
    var configValues = groups.Config.ToDictionary(v => v.Name, v => v.Value);

    var envArray = new JsonArray();
    foreach (var m in metadata)
    {
        var defaultValue = configValues.GetValueOrDefault(m.Name);

        var entry = new JsonObject { ["name"] = m.Name, ["label"] = m.Label };
        if (!string.IsNullOrEmpty(defaultValue))
            entry["default"] = defaultValue;
        if (m.Description is not null)
            entry["description"] = m.Description;

        envArray.Add((JsonNode)entry);
    }

    var root = new JsonObject
    {
        ["version"] = "2",
        ["templates"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = 3,
                ["title"] = "Nocturne",
                ["description"] = "Nightscout-compatible diabetes management API with Row Level Security and multi-tenancy.",
                ["categories"] = new JsonArray(
                    JsonValue.Create("diabetes"),
                    JsonValue.Create("nightscout"),
                    JsonValue.Create("healthcare")),
                ["platform"] = "linux",
                ["note"] = "After deployment, configure data connectors (Dexcom, LibreLinkUp, etc.) and chat bot integrations (Discord, Slack, Telegram, WhatsApp) through Settings → Administration in the Nocturne UI. Bot credentials are encrypted at rest.",
                ["repository"] = new JsonObject
                {
                    ["url"] = "https://github.com/nightscout/nocturne",
                    ["stackfile"] = "deploy/portainer/docker-compose.yaml"
                },
                ["env"] = envArray
            }
        }
    };

    return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
}

record EnvVarGroups(
    List<(string Name, string Value)> Config,
    List<(string Name, string Value)> RequiredConfig,
    List<(string Name, string Value)> Secrets,
    List<(string Name, string Value)> Optional);

record EnvVarMeta(string Name, string Label, string? Description, string? Default);

// scripts/publish-release.cs
//
// Generates the production Docker Compose bundle for GitHub Releases.
//
// Usage:
//   dotnet run scripts/publish-release.cs [output-dir]
//
// Requires: .NET 10 SDK, Aspire CLI

using System.Diagnostics;

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

    // Post-process compose: rename ugly auto-generated env var names
    var envVarRenames = new Dictionary<string, string>
    {
        ["NOCTURNE_POSTGRES_SERVER_BINDMOUNT_0"] = "POSTGRES_INIT_DIR",
    };

    var composeContent = File.ReadAllText(composePath);
    foreach (var (oldName, newName) in envVarRenames)
    {
        composeContent = composeContent.Replace(oldName, newName);
    }
    File.WriteAllText(Path.Combine(outputDir, "docker-compose.yaml"), composeContent);
    Console.WriteLine($"[publish-release] Wrote {Path.Combine(outputDir, "docker-compose.yaml")}");

    // Generate .env.example from aspire-generated .env
    var aspireEnvPath = Path.Combine(tempDir, ".env");
    GenerateEnvExample(aspireEnvPath, Path.Combine(outputDir, ".env.example"), envVarRenames);
    Console.WriteLine($"[publish-release] Wrote {Path.Combine(outputDir, ".env.example")}");

    // Copy init script
    var initScriptSource = Path.Combine(repoRoot, "docs", "postgres", "container-init", "00-init.sh");
    File.Copy(initScriptSource, Path.Combine(outputDir, "00-init.sh"), overwrite: true);
    Console.WriteLine($"[publish-release] Wrote {Path.Combine(outputDir, "00-init.sh")}");

    Console.WriteLine();
    Console.WriteLine("[publish-release] Done! Output files:");
    Console.WriteLine($"  {Path.Combine(outputDir, "docker-compose.yaml")}");
    Console.WriteLine($"  {Path.Combine(outputDir, ".env.example")}");
    Console.WriteLine($"  {Path.Combine(outputDir, "00-init.sh")}");

    return 0;
}
finally
{
    if (Directory.Exists(tempDir))
        Directory.Delete(tempDir, recursive: true);
}

static void GenerateEnvExample(
    string aspireEnvPath,
    string outputPath,
    Dictionary<string, string> renames)
{
    // Known defaults for non-secret values
    var defaults = new Dictionary<string, string>
    {
        ["NOCTURNE_API_IMAGE"] = "ghcr.io/nightscout/nocturne/nocturne-api:latest",
        ["NOCTURNE_WEB_IMAGE"] = "ghcr.io/nightscout/nocturne/nocturne-web:latest",
        ["NOCTURNE_API_PORT"] = "8080",
        ["POSTGRES_USERNAME"] = "nocturne",
        ["POSTGRES_INIT_DIR"] = "./init",
        ["NOCTURNE_POSTGRES_SERVER_BINDMOUNT_0"] = "./init",
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
        "PUBLIC_BASE_DOMAIN",
    };

    // Optional vars
    var optional = new HashSet<string>
    {
        "DISCORD_BOT_TOKEN",
        "TELEGRAM_BOT_TOKEN",
        "SLACK_BOT_TOKEN",
        "WHATSAPP_ACCESS_TOKEN",
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
            var renamedName = renames.GetValueOrDefault(name, name);

            if (!seenVars.Add(renamedName)) continue;

            if (secrets.Contains(renamedName))
                secretVars.Add((renamedName, ""));
            else if (requiredConfig.Contains(renamedName))
                requiredConfigVars.Add((renamedName, ""));
            else if (optional.Contains(renamedName))
                optionalVars.Add((renamedName, defaults.GetValueOrDefault(renamedName, "")));
            else
                configVars.Add((renamedName, defaults.GetValueOrDefault(renamedName, defaults.GetValueOrDefault(name, ""))));
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
        writer.WriteLine("POSTGRES_INIT_DIR=./init");
        writer.WriteLine();
        writer.WriteLine("PUBLIC_BASE_DOMAIN=");
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

// scripts/build.cs
//
// Build Nocturne containers locally or in CI.
//
// Usage:
//   dotnet run scripts/build.cs                     # build with tag "dev", no push
//   dotnet run scripts/build.cs v1.2.3              # build with tag "v1.2.3", no push
//   dotnet run scripts/build.cs latest --push       # build and push with tag "latest"
//
// Environment variables (optional):
//   REGISTRY          Container registry       (default: ghcr.io)
//   IMAGE_REPOSITORY  Image repository         (default: detected from git remote)
//   CONTAINER_RID     .NET RID for API image   (default: linux-x64)
//   SKIP_API          Skip API container build (default: false)
//   SKIP_WEB          Skip Web container build (default: false)

using System.Diagnostics;

var repoRoot = Directory.GetCurrentDirectory();
var version = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : "dev";
var push = args.Contains("--push");
var registry = Environment.GetEnvironmentVariable("REGISTRY") ?? "ghcr.io";
var imageRepository = Environment.GetEnvironmentVariable("IMAGE_REPOSITORY");
var skipApi = Environment.GetEnvironmentVariable("SKIP_API") == "true";
var skipWeb = Environment.GetEnvironmentVariable("SKIP_WEB") == "true";
var containerRid = Environment.GetEnvironmentVariable("CONTAINER_RID") ?? "linux-x64";

if (string.IsNullOrEmpty(imageRepository))
{
    var remoteUrl = RunCapture("git", ["remote", "get-url", "origin"]).Trim();
    imageRepository = ExtractGitHubRepo(remoteUrl);
}

Console.WriteLine("==> Build configuration");
Console.WriteLine($"    Version:    {version}");
Console.WriteLine($"    Registry:   {registry}");
Console.WriteLine($"    Repository: {imageRepository}");
Console.WriteLine($"    Push:       {(push ? "yes" : "no")}");
Console.WriteLine();

// Step 1: Prepare
Console.WriteLine("==> Preparing build environment");

Console.WriteLine("    Restoring .NET dependencies");
Run("dotnet", ["restore", "--verbosity", "quiet"]);

Console.WriteLine("    Installing web dependencies");
Run("pnpm", ["install", "--frozen-lockfile"], workingDir: Path.Combine(repoRoot, "src", "Web"));

Console.WriteLine("    Building bridge package");
Run("pnpm", ["run", "build"], workingDir: Path.Combine(repoRoot, "src", "Web", "packages", "bridge"));

// Step 1b: Generate architectural diagrams
Console.WriteLine("==> Generating architectural diagrams");
Run("dotnet", ["tool", "restore", "--verbosity", "quiet"]);
Run("dotnet", ["build", "tools/Nocturne.Tools.DiagramGen/Nocturne.Tools.DiagramGen.csproj", "-c", "Release", "--verbosity", "quiet"]);
Run("bash", ["scripts/diagrams/generate-diagrams.sh"]);

// Step 2: Generate API client
Console.WriteLine("==> Generating API client");
Run("dotnet", ["build", "-c", "Release", "src/API/Nocturne.API/Nocturne.API.csproj", "--verbosity", "quiet"]);

// Step 3: Verify generated files
Console.WriteLine("==> Verifying generated API client files");
var generatedDir = Path.Combine(repoRoot, "src", "Web", "packages", "app", "src", "lib", "api", "generated");
var requiredFiles = new[] { "passkeys", "patientRecords", "chartDatas", "profiles", "alerts" };
var missing = requiredFiles.Where(f => !File.Exists(Path.Combine(generatedDir, $"{f}.generated.remote.ts"))).ToList();

if (missing.Count > 0)
{
    Console.Error.WriteLine($"ERROR: Missing generated remote files: {string.Join(", ", missing)}");
    return 1;
}

var remoteCount = Directory.GetFiles(generatedDir, "*.generated.remote.ts").Length;
Console.WriteLine($"    Found {remoteCount} generated remote files");
if (remoteCount < 40)
{
    Console.Error.WriteLine($"ERROR: Expected at least 40 generated remote files but found only {remoteCount}");
    return 1;
}

// Step 4: Build API container
if (!skipApi)
{
    Console.WriteLine("==> Building API container");
    var publishArgs = new List<string>
    {
        "publish",
        "src/API/Nocturne.API/Nocturne.API.csproj",
        "-c", "Release",
        "-r", containerRid,
        "-p:PublishProfile=DefaultContainer",
        $"-p:ContainerRepository={imageRepository}/nocturne-api",
        $"-p:ContainerImageTag={version}",
    };

    if (push)
        publishArgs.Add($"-p:ContainerRegistry={registry}");

    Run("dotnet", [.. publishArgs]);
    Console.WriteLine($"    Tagged: {imageRepository}/nocturne-api:{version}");
}
else
{
    Console.WriteLine("==> Skipping API container (SKIP_API=true)");
}

// Step 5: Build Web container
if (!skipWeb)
{
    Console.WriteLine("==> Building Web container");
    var dockerArgs = new List<string>
    {
        "buildx", "build",
        "--platform", "linux/amd64",
        "--tag", $"{registry}/{imageRepository}/nocturne-web:{version}",
        "--file", Path.Combine(repoRoot, "Dockerfile.web"),
    };

    if (push)
        dockerArgs.Add("--push");
    else
        dockerArgs.Add("--load");

    dockerArgs.Add(repoRoot);
    Run("docker", [.. dockerArgs]);
    Console.WriteLine($"    Tagged: {registry}/{imageRepository}/nocturne-web:{version}");
}
else
{
    Console.WriteLine("==> Skipping Web container (SKIP_WEB=true)");
}

Console.WriteLine();
Console.WriteLine("==> Build complete!");
Console.WriteLine($"    nocturne-api:{version}");
Console.WriteLine($"    nocturne-web:{version}");

return 0;

// -- Helpers ---------------------------------------------------------------

static string ExtractGitHubRepo(string remoteUrl)
{
    // Handles both HTTPS and SSH remote URLs
    // https://github.com/owner/repo.git -> owner/repo
    // git@github.com:owner/repo.git    -> owner/repo
    var match = System.Text.RegularExpressions.Regex.Match(
        remoteUrl, @"github\.com[:/](.+?)(?:\.git)?$");
    return match.Success ? match.Groups[1].Value.ToLowerInvariant() : throw new InvalidOperationException(
        $"Could not detect IMAGE_REPOSITORY from git remote: {remoteUrl}. Set IMAGE_REPOSITORY env var.");
}

static void Run(string command, string[] arguments, string? workingDir = null)
{
    var psi = new ProcessStartInfo(command)
    {
        UseShellExecute = false,
        WorkingDirectory = workingDir ?? Directory.GetCurrentDirectory(),
    };
    foreach (var arg in arguments) psi.ArgumentList.Add(arg);

    using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start: {command}");
    process.WaitForExit();

    if (process.ExitCode != 0)
        throw new InvalidOperationException($"Command failed with exit code {process.ExitCode}: {command} {string.Join(' ', arguments)}");
}

static string RunCapture(string command, string[] arguments)
{
    var psi = new ProcessStartInfo(command)
    {
        RedirectStandardOutput = true,
        UseShellExecute = false,
    };
    foreach (var arg in arguments) psi.ArgumentList.Add(arg);

    using var process = Process.Start(psi)!;
    var output = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    return output;
}

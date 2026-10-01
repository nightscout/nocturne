// scripts/build.cs
//
// Build Nocturne containers locally or in CI.
//
// Usage:
//   dotnet run scripts/build.cs                     # build with tag "dev", no push
//   dotnet run scripts/build.cs v1.2.3              # build with tag "v1.2.3", no push
//   dotnet run scripts/build.cs develop --push      # build and push with tag "develop"
//
// ":latest" is the latest release and ":develop" follows main; CI publishes both
// (.github/workflows/docker-publish.yml), so a local push should use neither without reason.
//
// Environment variables (optional):
//   REGISTRY          Container registry       (default: ghcr.io)
//   IMAGE_REPOSITORY  Image repository         (default: detected from git remote)
//   CONTAINER_RID       .NET RID for API image     (default: host arch — linux-x64 or linux-arm64)
//   CONTAINER_PLATFORM  Docker platform for Web    (default: host arch — linux/amd64 or linux/arm64)
//   SKIP_API          Skip API container build (default: false)
//   SKIP_WEB          Skip Web container build (default: false)

#:project Shared/Shared.csproj

using System.Diagnostics;
using static ProcessHelpers;

var repoRoot = Directory.GetCurrentDirectory();
var version = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : "dev";
var push = args.Contains("--push");
var registry = Environment.GetEnvironmentVariable("REGISTRY") ?? "ghcr.io";
var imageRepository = Environment.GetEnvironmentVariable("IMAGE_REPOSITORY");
var skipApi = Environment.GetEnvironmentVariable("SKIP_API") == "true";
var skipWeb = Environment.GetEnvironmentVariable("SKIP_WEB") == "true";
var hostIsArm64 = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64;
var containerRid = Environment.GetEnvironmentVariable("CONTAINER_RID") ?? (hostIsArm64 ? "linux-arm64" : "linux-x64");
var containerPlatform = Environment.GetEnvironmentVariable("CONTAINER_PLATFORM") ?? (hostIsArm64 ? "linux/arm64" : "linux/amd64");

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
    // The API packs the nocturne_alerts cdylib only when it exists at publish time; without it
    // the image cannot run Alerts:Engine=rust and scoped DND classifies every rule as undirected.
    Console.WriteLine("==> Building nocturne_alerts native library");
    var rustTriple = containerRid switch
    {
        "linux-x64" => "x86_64-unknown-linux-gnu",
        "linux-arm64" => "aarch64-unknown-linux-gnu",
        _ => null,
    };
    if (rustTriple is null)
    {
        Console.Error.WriteLine($"ERROR: No Rust target for CONTAINER_RID={containerRid}; expected linux-x64 or linux-arm64");
        return 1;
    }
    var cargoTargetRoot = BuildNativeLibrary(repoRoot, rustTriple);
    if (cargoTargetRoot is null)
        return 1;
    var nativeDir = Path.Combine(cargoTargetRoot, rustTriple, "release");
    if (!File.Exists(Path.Combine(nativeDir, "libnocturne_alerts.so")))
    {
        Console.Error.WriteLine($"ERROR: the build finished but {Path.Combine(nativeDir, "libnocturne_alerts.so")} does not exist");
        return 1;
    }

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
    // The csproj packs linux-arm64 from <cargo target root>/aarch64-unknown-linux-gnu/release;
    // the linux-x64 slot reads NocturneAlertsNativeDir, which defaults to the host build.
    if (containerRid == "linux-x64")
        publishArgs.Add($"-p:NocturneAlertsNativeDir={nativeDir}{Path.DirectorySeparatorChar}");
    else
        publishArgs.Add($"-p:NocturneAlertsCargoTargetRoot={cargoTargetRoot}{Path.DirectorySeparatorChar}");

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
        "--platform", containerPlatform,
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

// Builds libnocturne_alerts.so for the image's architecture and returns the cargo target
// directory it lands under (<root>/<triple>/release), or null after printing why it could not.
// A Linux host of the same architecture builds with its own cargo. Anything else needs a
// linker for the target: cargo-zigbuild when installed, a <arch>-linux-gnu-gcc cross linker on
// Linux (as docker-publish.yml uses), or else a Debian rust container, whose glibc is older than
// the API base image's so the library loads there.
static string? BuildNativeLibrary(string repoRoot, string rustTriple)
{
    var crates = Path.Combine(repoRoot, "crates");
    var hostTarget = Path.Combine(crates, "target");
    var targetArch = rustTriple.Split('-')[0];
    var hostArch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.X64 => "x86_64",
        System.Runtime.InteropServices.Architecture.Arm64 => "aarch64",
        _ => null,
    };
    string[] cargoArgs = ["build", "--release", "-p", "nocturne-alerts-ffi", "--target", rustTriple];

    if (OperatingSystem.IsLinux() && hostArch == targetArch && Works("cargo", ["--version"]))
    {
        Run("cargo", cargoArgs, workingDir: crates);
        return hostTarget;
    }

    if (Works("cargo", ["zigbuild", "--version"]))
    {
        Console.WriteLine("    Cross-compiling with cargo-zigbuild");
        Run("cargo", ["zigbuild", .. cargoArgs[1..]], workingDir: crates);
        return hostTarget;
    }

    var crossLinker = $"{targetArch}-linux-gnu-gcc";
    if (OperatingSystem.IsLinux() && Works("cargo", ["--version"]) && Works(crossLinker, ["--version"]))
    {
        Console.WriteLine($"    Cross-compiling with {crossLinker}");
        Environment.SetEnvironmentVariable(
            $"CARGO_TARGET_{rustTriple.ToUpperInvariant().Replace('-', '_')}_LINKER", crossLinker);
        Run("cargo", cargoArgs, workingDir: crates);
        return hostTarget;
    }

    if (Works("docker", ["version", "--format", "{{.Server.Arch}}"]))
        return BuildNativeLibraryInContainer(crates, rustTriple, targetArch);

    Console.Error.WriteLine($"ERROR: cannot build libnocturne_alerts.so for {rustTriple} on this host.");
    Console.Error.WriteLine("       Start Docker, install cargo-zigbuild (plus: rustup target add " + rustTriple + "),");
    Console.Error.WriteLine($"       or on Linux install {crossLinker}; or set SKIP_API=true.");
    return null;
}

// Builds in rust:1-bookworm of the Docker engine's own architecture, adding a cross linker when
// the image targets the other one. The target directory is crates/target/container, so the
// host's own cargo builds are left alone.
static string BuildNativeLibraryInContainer(string crates, string rustTriple, string targetArch)
{
    var engineArch = RunCapture("docker", ["version", "--format", "{{.Server.Arch}}"]).Trim() switch
    {
        "amd64" => "x86_64",
        "arm64" => "aarch64",
        var other => other,
    };
    var script = new List<string>();
    var env = new List<string> { "-e", "CARGO_TARGET_DIR=/src/target/container" };
    if (engineArch != targetArch)
    {
        var gccPackage = targetArch == "x86_64" ? "gcc-x86-64-linux-gnu" : "gcc-aarch64-linux-gnu";
        script.Add($"apt-get update -qq && apt-get install -y -qq {gccPackage} >/dev/null");
        script.Add($"rustup target add {rustTriple}");
        env.AddRange(["-e", $"CARGO_TARGET_{rustTriple.ToUpperInvariant().Replace('-', '_')}_LINKER={targetArch}-linux-gnu-gcc"]);
    }
    script.Add($"cargo build --release -p nocturne-alerts-ffi --target {rustTriple}");
    if (OperatingSystem.IsLinux())
    {
        var owner = $"{RunCapture("id", ["-u"]).Trim()}:{RunCapture("id", ["-g"]).Trim()}";
        script.Add($"chown -R {owner} /src/target/container");
    }

    Console.WriteLine($"    Building in rust:1-bookworm ({engineArch} engine, {targetArch} target)");
    Run("docker",
    [
        "run", "--rm",
        "--mount", $"type=bind,source={crates},target=/src",
        "-v", "nocturne-cargo-registry:/usr/local/cargo/registry",
        .. env,
        "-w", "/src",
        "rust:1-bookworm",
        "sh", "-ec", string.Join(" && ", script),
    ]);
    return Path.Combine(crates, "target", "container");
}

// Whether the command starts and exits 0, with its output discarded.
static bool Works(string command, string[] arguments)
{
    var psi = new ProcessStartInfo(command)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    foreach (var arg in arguments) psi.ArgumentList.Add(arg);
    try
    {
        using var process = Process.Start(psi)!;
        _ = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return process.ExitCode == 0;
    }
    catch (System.ComponentModel.Win32Exception)
    {
        return false;
    }
}

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

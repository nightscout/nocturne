using System.Diagnostics;
using static ProcessHelpers;

/// <summary>
/// Builds the nocturne_alerts cdylib (crates/nocturne-alerts-ffi) for a Linux container image. The
/// API refuses to start without it, and its csproj packs it into the image only when it exists at
/// publish time, so every API image build runs this first.
/// </summary>
public static class AlertsNativeBuild
{
    /// <summary>
    /// Builds <c>libnocturne_alerts.so</c> for <paramref name="containerRid"/> (<c>linux-x64</c> or
    /// <c>linux-arm64</c>) and returns the <c>dotnet publish</c> properties that pack it, or null
    /// after printing why it could not.
    /// </summary>
    /// <remarks>
    /// The csproj packs linux-arm64 from <c>&lt;cargo target root&gt;/aarch64-unknown-linux-gnu/release</c>;
    /// the linux-x64 slot reads <c>NocturneAlertsNativeDir</c>.
    /// </remarks>
    public static string[]? BuildForContainer(string repoRoot, string containerRid)
    {
        var rustTriple = containerRid switch
        {
            "linux-x64" => "x86_64-unknown-linux-gnu",
            "linux-arm64" => "aarch64-unknown-linux-gnu",
            _ => null,
        };
        if (rustTriple is null)
        {
            Console.Error.WriteLine($"ERROR: No Rust target for {containerRid}; expected linux-x64 or linux-arm64");
            return null;
        }

        var cargoTargetRoot = Build(repoRoot, rustTriple);
        if (cargoTargetRoot is null)
            return null;
        var nativeDir = Path.Combine(cargoTargetRoot, rustTriple, "release");
        if (!File.Exists(Path.Combine(nativeDir, "libnocturne_alerts.so")))
        {
            Console.Error.WriteLine($"ERROR: the build finished but {Path.Combine(nativeDir, "libnocturne_alerts.so")} does not exist");
            return null;
        }

        return containerRid == "linux-x64"
            ? [$"-p:NocturneAlertsNativeDir={nativeDir}{Path.DirectorySeparatorChar}", "-p:NocturneAlertsFromSource=true"]
            : [$"-p:NocturneAlertsCargoTargetRoot={cargoTargetRoot}{Path.DirectorySeparatorChar}", "-p:NocturneAlertsFromSource=true"];
    }

    // Builds libnocturne_alerts.so for the image's architecture and returns the cargo target
    // directory it lands under (<root>/<triple>/release), or null after printing why it could not.
    // A Linux host of the same architecture builds with its own cargo. Anything else needs a
    // linker for the target: cargo-zigbuild when installed, a <arch>-linux-gnu-gcc cross linker on
    // Linux (as docker-publish.yml uses), or else a Debian rust container, whose glibc is older than
    // the API base image's so the library loads there.
    private static string? Build(string repoRoot, string rustTriple)
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
            return BuildInContainer(crates, rustTriple, targetArch);

        Console.Error.WriteLine($"ERROR: cannot build libnocturne_alerts.so for {rustTriple} on this host.");
        Console.Error.WriteLine("       Start Docker, install cargo-zigbuild (plus: rustup target add " + rustTriple + "),");
        Console.Error.WriteLine($"       or on Linux install {crossLinker}.");
        return null;
    }

    // Builds in rust:1-bookworm of the Docker engine's own architecture, adding a cross linker when
    // the image targets the other one. The target directory is crates/target/container, so the
    // host's own cargo builds are left alone.
    private static string BuildInContainer(string crates, string rustTriple, string targetArch)
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
    private static bool Works(string command, string[] arguments)
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
}

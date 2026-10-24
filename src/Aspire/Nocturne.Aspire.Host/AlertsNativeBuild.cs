using System.Diagnostics;
using Aspire.Hosting.ApplicationModel;

namespace Nocturne.Aspire.Host;

/// <summary>
/// The run-mode cargo build of the nocturne_alerts native library, which the API refuses to start
/// without.
/// </summary>
/// <remarks>
/// With Rust installed, the API's build packs the library from crates/target/release, so the cargo
/// build finishes first; without, the build downloads the library main published for this crates/
/// tree (Nocturne.Core.Alerts.Native.csproj). The API loads the packed copy, never the cargo
/// output: on Windows a loaded library cannot be replaced, and a later cargo build would fail on it.
/// </remarks>
internal static class AlertsNativeBuild
{
    public const string ResourceName = "nocturne-alerts-native";

    /// <summary>
    /// Adds the cargo build as a resource <paramref name="api"/> waits for, when
    /// <paramref name="cargoAvailable"/>; otherwise says the API build will fetch the library.
    /// </summary>
    public static void AddTo<T>(
        IDistributedApplicationBuilder builder, IResourceBuilder<T> api, string solutionRoot, bool cargoAvailable)
        where T : IResourceWithWaitSupport
    {
        if (!cargoAvailable)
        {
            Console.WriteLine(
                "[Nocturne.Aspire] cargo is not on PATH, so the alert engine is not built here: the API build "
                + "packs the library main published for this crates/ tree, and fails if there is none.");
            return;
        }

        var cargoBuild = builder
            .AddExecutable(ResourceName, "cargo", Path.Combine(solutionRoot, "crates"),
                "build", "--release", "-p", "nocturne-alerts-ffi")
            .WithParentRelationship(api.Resource);
        api.WaitForCompletion(cargoBuild);
    }

    /// <summary>Whether <paramref name="command"/> starts from <c>PATH</c> and exits 0 within 10 s.</summary>
    public static bool CommandRuns(string command, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(command, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
                return false;
            if (!process.WaitForExit(10_000))
            {
                process.Kill(entireProcessTree: true);
                return false;
            }
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}

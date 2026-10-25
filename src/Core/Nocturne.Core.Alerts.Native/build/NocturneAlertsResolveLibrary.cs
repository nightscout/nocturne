using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

/// <summary>
/// Chooses the nocturne_alerts library a developer build packs (Nocturne.Core.Alerts.Native.csproj
/// explains the rule) and stages it at <c>current/</c> under <see cref="CacheRoot"/>.
/// </summary>
/// <remarks>
/// Compiled by RoslynCodeTaskFactory at build time; not part of the assembly.
/// <see cref="Mode"/> is <c>local</c> or <c>prebuilt</c> with <see cref="Library"/> set, or
/// <c>none</c> with <see cref="Detail"/> saying why.
/// </remarks>
public sealed class NocturneAlertsResolveLibrary : Task
{
    private static readonly TimeSpan MissingRecheck = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(30);

    [Required] public string CratesRoot { get; set; } = "";
    [Required] public string LocalLibrary { get; set; } = "";
    [Required] public string FileName { get; set; } = "";
    public string PublishedRid { get; set; } = "";
    [Required] public string CacheRoot { get; set; } = "";
    [Required] public string Repository { get; set; } = "";
    public bool Download { get; set; } = true;
    public bool VerifyAttestation { get; set; } = true;

    [Output] public string Mode { get; set; } = "none";
    [Output] public string Library { get; set; } = "";
    [Output] public string Detail { get; set; } = "";
    [Output] public string Caution { get; set; } = "";

    public override bool Execute()
    {
        Directory.CreateDirectory(CacheRoot);
        // Builds of several projects can resolve at once; one at a time touches the cache.
        using var cacheLock = AcquireLock(Path.Combine(CacheRoot, ".lock"));

        if (LocalIsFresh())
        {
            Stage(LocalLibrary, "local:" + File.GetLastWriteTimeUtc(LocalLibrary).Ticks);
            Mode = "local";
            return true;
        }

        var (statusExit, changes) = Git(CratesRoot, "status --porcelain -- .");
        if (statusExit != 0)
            return None("git could not read crates/, so the published library for it cannot be found");
        if (changes.Length > 0)
            return None("crates/ has local changes and no local build is newer than them");

        var (treeExit, tree) = Git(Path.GetDirectoryName(CratesRoot)!, "rev-parse HEAD:crates");
        if (treeExit != 0 || tree.Length != 40)
            return None("git could not name the crates/ tree");
        if (string.IsNullOrEmpty(PublishedRid))
            return None("no library is published for this platform");

        var treeDir = Path.Combine(CacheRoot, tree);
        var cached = Path.Combine(treeDir, FileName);
        if (!File.Exists(cached))
        {
            if (!Download)
                return None("there is no local build newer than crates/, and NocturneAlertsDownload is false");
            var failure = Fetch(tree, treeDir, cached);
            if (failure is not null)
                return None(failure);
        }

        Stage(cached, "tree:" + tree);
        Mode = "prebuilt";
        Detail = "using the library main published for crates tree " + tree;
        return true;
    }

    private bool None(string detail)
    {
        Mode = "none";
        Detail = detail;
        return true;
    }

    /// <summary>
    /// A local build exists and none of its inputs is newer: the sources cargo's dep-info file
    /// (beside the library) lists for it, and every Cargo.toml and Cargo.lock. Files cargo did not
    /// build it from, such as tests, do not count, so a branch switch that touches only those
    /// leaves it fresh, as cargo itself does.
    /// </summary>
    private bool LocalIsFresh()
    {
        var depInfo = Path.ChangeExtension(LocalLibrary, ".d");
        if (!File.Exists(LocalLibrary) || !File.Exists(depInfo))
            return false;

        // "<outputs>: <dep> <dep> …", spaces inside a path escaped as "\ ".
        var rule = File.ReadLines(depInfo).FirstOrDefault() ?? "";
        var separator = rule.IndexOf(": ", StringComparison.Ordinal);
        if (separator < 0)
            return false;
        var sources = rule.Substring(separator + 2).Replace("\\ ", "\0")
            .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(dep => dep.Replace('\0', ' '));

        var crateDirs = Directory.EnumerateDirectories(CratesRoot)
            .Where(d => !string.Equals(Path.GetFileName(d), "target", StringComparison.OrdinalIgnoreCase));
        var manifests = crateDirs.Select(d => Path.Combine(d, "Cargo.toml"))
            .Append(Path.Combine(CratesRoot, "Cargo.toml"))
            .Append(Path.Combine(CratesRoot, "Cargo.lock"))
            .Where(File.Exists);

        var built = File.GetLastWriteTimeUtc(LocalLibrary);
        return sources.Concat(manifests).All(f => File.Exists(f) && File.GetLastWriteTimeUtc(f) <= built);
    }

    /// <summary>
    /// Copies <paramref name="source"/> to the one path the build packs, restamping it whenever
    /// <paramref name="identity"/> changes so that PreserveNewest copies it on to bin/.
    /// </summary>
    private void Stage(string source, string identity)
    {
        var currentDir = Path.Combine(CacheRoot, "current");
        Directory.CreateDirectory(currentDir);
        var current = Path.Combine(currentDir, FileName);
        var marker = Path.Combine(currentDir, "source");
        if (!File.Exists(current) || !File.Exists(marker) || File.ReadAllText(marker) != identity)
        {
            File.Copy(source, current, overwrite: true);
            File.SetLastWriteTimeUtc(current, DateTime.UtcNow);
            File.WriteAllText(marker, identity);
        }
        Library = current;
    }

    /// <summary>Downloads and verifies the published library; null on success, else why not.</summary>
    private string? Fetch(string tree, string treeDir, string cached)
    {
        Directory.CreateDirectory(treeDir);
        var missing = Path.Combine(treeDir, ".missing");
        if (File.Exists(missing) && DateTime.UtcNow - File.GetLastWriteTimeUtc(missing) < MissingRecheck)
            return "main has not published crates tree " + tree + " (checked in the last 10 minutes)";

        var asset = "nocturne_alerts-" + tree + "-" + PublishedRid + Path.GetExtension(FileName);
        var url = "https://github.com/" + Repository + "/releases/download/alerts-native/" + asset;
        var part = cached + ".part";
        try
        {
            using var http = new HttpClient { Timeout = DownloadTimeout };
            using var response = http.GetAsync(url).GetAwaiter().GetResult();
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                File.WriteAllText(missing, url);
                return "main has not published crates tree " + tree + " yet";
            }
            response.EnsureSuccessStatusCode();
            using (var file = File.Create(part))
                response.Content.CopyToAsync(file).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Threading.Tasks.TaskCanceledException or IOException)
        {
            TryDelete(part);
            return "downloading " + url + " failed (" + ex.Message + ")";
        }

        if (new FileInfo(part).Length == 0)
        {
            TryDelete(part);
            return "the published library for crates tree " + tree + " is empty";
        }

        if (VerifyAttestation)
        {
            var (verifyExit, output) = Run("gh",
                "attestation verify \"" + part + "\" -R " + Repository
                + " --signer-workflow " + Repository + "/.github/workflows/alerts-native-publish.yml"
                + " --source-ref refs/heads/main");
            if (verifyExit == -1)
            {
                Caution = "the downloaded library's provenance was not checked: install the GitHub CLI (gh) to verify it";
            }
            else if (verifyExit != 0)
            {
                TryDelete(part);
                return "the downloaded library failed `gh attestation verify` (" + output.Trim() + "); "
                    + "run `gh auth login`, or set NocturneAlertsVerifyAttestation=false";
            }
        }

        File.Move(part, cached);
        return null;
    }

    private static (int Exit, string Output) Git(string workingDirectory, string arguments) =>
        Run("git", arguments, workingDirectory);

    /// <returns>Exit code and trimmed stdout, or -1 when the command cannot start.</returns>
    private static (int Exit, string Output) Run(string command, string arguments, string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo(command, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (workingDirectory is not null)
            psi.WorkingDirectory = workingDirectory;
        try
        {
            using var process = Process.Start(psi)!;
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, (output + error.GetAwaiter().GetResult()).Trim());
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (-1, "");
        }
    }

    private static FileStream AcquireLock(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (attempt < 600)
            {
                Thread.Sleep(100);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
    }
}

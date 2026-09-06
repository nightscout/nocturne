using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace Nocturne.Aspire.Hosting;

/// <summary>
/// Manages mkcert-issued TLS certificates for local development with custom domains.
/// Certificates are stored in ~/.nocturne/certs/ (user-level, shared across worktrees).
/// </summary>
public static class MkcertHelper
{
    private static readonly string CertsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".nocturne",
        "certs"
    );

    /// <summary>
    /// Returns an X509Certificate2 for the given domain, generating one via mkcert if needed.
    /// </summary>
    public static X509Certificate2 EnsureCertificate(string domain)
    {
        var certFile = Path.Combine(CertsDir, $"{domain}-cert.pem");
        var keyFile = Path.Combine(CertsDir, $"{domain}-key.pem");

        if (File.Exists(certFile) && File.Exists(keyFile))
        {
            Console.WriteLine($"[Nocturne.Aspire] Using existing mkcert certificate for {domain}");
            return X509Certificate2.CreateFromPemFile(certFile, keyFile);
        }

        EnsureMkcertInstalled();
        GenerateCertificate(domain, certFile, keyFile);

        return X509Certificate2.CreateFromPemFile(certFile, keyFile);
    }

    /// <summary>
    /// Like <see cref="EnsureCertificate"/>, but returns null instead of throwing when
    /// mkcert is not installed. Used for the default local domain, where the caller can
    /// fall back to the ASP.NET developer certificate.
    /// </summary>
    public static X509Certificate2? TryEnsureCertificate(string domain)
    {
        try
        {
            return EnsureCertificate(domain);
        }
        // CryptographicException: a corrupt/truncated cached PEM (e.g. a run
        // killed mid-generation) must not brick every subsequent start.
        catch (Exception ex) when (ex is InvalidOperationException
            or System.Security.Cryptography.CryptographicException)
        {
            Console.WriteLine($"[Nocturne.Aspire] {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Checks whether the domain resolves to a loopback address and prints a warning if not.
    /// Subdomains of .localhost are exempt: browsers resolve them to loopback themselves,
    /// so an OS-resolver miss is not actionable.
    /// </summary>
    public static void WarnIfDomainUnresolvable(string domain, int port)
    {
        if (domain == "localhost" || domain.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            var addresses = Dns.GetHostAddresses(domain);
            var hasLoopback = addresses.Any(IPAddress.IsLoopback);

            if (hasLoopback)
                return;
        }
        catch (SocketException)
        {
            // Domain doesn't resolve at all — fall through to warning.
        }

        var hostsPath = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? @"C:\Windows\System32\drivers\etc\hosts"
            : "/etc/hosts";

        var portHint = port > 0 ? $" (port {port})" : " (dynamic port)";

        Console.WriteLine();
        Console.WriteLine($"[Nocturne.Aspire] WARNING: '{domain}' does not resolve to loopback.");
        Console.WriteLine($"  Add the following to {hostsPath}:");
        Console.WriteLine($"    127.0.0.1  {domain}");
        Console.WriteLine($"    127.0.0.1  <your-tenant>.{domain}");
        Console.WriteLine();
        Console.WriteLine("  Note: hosts files don't support wildcards. Add one line per tenant slug.");
        Console.WriteLine($"  Then access the app at https://<your-tenant>.{domain}{portHint}");
        Console.WriteLine();
    }

    private static void EnsureMkcertInstalled()
    {
        try
        {
            RunProcess("mkcert", "-version");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            string installHint;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                installHint = "winget install FiloSottile.mkcert";
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                installHint = "brew install mkcert";
            else
                installHint = "your package manager (e.g. apt install mkcert)";

            throw new InvalidOperationException(
                $"mkcert is not installed or not on PATH. Install it with: {installHint}"
            );
        }
    }

    private static void GenerateCertificate(string domain, string certFile, string keyFile)
    {
        Directory.CreateDirectory(CertsDir);

        // Idempotent: installs the local CA into the system trust store if not already done.
        // mkcert -install may exit non-zero if a non-critical trust store (e.g. Android Studio's
        // keytool) is inaccessible. As long as the system trust store has the CA, that's fine.
        RunProcess("mkcert", "-install", ignoreExitCode: true);

        // Generate cert for wildcard, apex, and localhost.
        RunProcess(
            "mkcert",
            $"-cert-file \"{certFile}\" -key-file \"{keyFile}\" \"*.{domain}\" \"{domain}\" localhost"
        );

        Console.WriteLine(
            $"[Nocturne.Aspire] Generated mkcert certificate for {domain} in {CertsDir}"
        );
    }

    private static string RunProcess(string fileName, string arguments, bool ignoreExitCode = false)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {fileName}");

        // Read stderr async to avoid deadlock when pipe buffers fill.
        var errorTask = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd().Trim();
        var error = errorTask.GetAwaiter().GetResult().Trim();

        if (!process.WaitForExit(30_000))
        {
            process.Kill();
            throw new InvalidOperationException(
                $"{fileName} timed out after 30 seconds. If mkcert -install prompted for a password, run it manually first.");
        }

        if (process.ExitCode != 0 && !ignoreExitCode)
        {
            throw new InvalidOperationException(
                $"{fileName} {arguments} failed (exit code {process.ExitCode}): {error}"
            );
        }

        return output;
    }
}

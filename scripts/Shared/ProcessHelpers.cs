using System.Diagnostics;

public static class ProcessHelpers
{
    /// <summary>
    /// The executable to start for <paramref name="command"/>. On Windows a bare name is looked up
    /// on PATH with PATHEXT, so a <c>.cmd</c> shim such as <c>pnpm.cmd</c> is found, which process
    /// start does not do itself; and <c>bash</c> is Git for Windows' bash, never WSL's, since the
    /// scripts expect the checkout's paths and tools.
    /// </summary>
    public static string ResolveCommand(string command)
    {
        if (!OperatingSystem.IsWindows() || Path.IsPathRooted(command) || Path.HasExtension(command))
            return command;

        if (command == "bash")
            return GitBash() ?? throw new InvalidOperationException(
                "bash was not found: install Git for Windows (https://git-scm.com/download/win), " +
                "whose bash these scripts run; WSL's bash is not used.");

        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var dir in SearchPath())
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(dir, command + extension);
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        return command;
    }

    /// <summary>
    /// <c>binash.exe</c> in the Git for Windows install whose <c>git.exe</c> is on PATH (in its
    /// <c>cmd</c>, <c>bin</c> or <c>mingw64in</c>), else in the default install location.
    /// </summary>
    private static string? GitBash()
    {
        var roots = SearchPath()
            .Where(dir => File.Exists(Path.Combine(dir, "git.exe")))
            .SelectMany(dir => new[] { Path.Combine(dir, ".."), Path.Combine(dir, "..", "..") })
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git"));
        return roots
            .Select(root => Path.GetFullPath(Path.Combine(root, "bin", "bash.exe")))
            .FirstOrDefault(File.Exists);
    }

    private static IEnumerable<string> SearchPath() =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Runs a command, streaming output directly. Throws on non-zero exit.</summary>
    public static void Run(string command, string[] arguments, string? workingDir = null)
    {
        var psi = new ProcessStartInfo(ResolveCommand(command))
        {
            UseShellExecute = false,
            WorkingDirectory = workingDir ?? Directory.GetCurrentDirectory(),
        };
        foreach (var arg in arguments) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start: {command}");
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Command failed with exit code {process.ExitCode}: {command} {string.Join(' ', arguments)}");
    }

    /// <summary>Runs a command and returns captured stdout. Throws on non-zero exit.</summary>
    public static string RunCapture(string command, string[] arguments)
    {
        var psi = new ProcessStartInfo(ResolveCommand(command))
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var arg in arguments) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Command failed with exit code {process.ExitCode}: {command} {string.Join(' ', arguments)}");

        return output;
    }

    /// <summary>
    /// Runs a command, capturing and forwarding stdout/stderr to the console.
    /// Returns the exit code rather than throwing.
    /// </summary>
    public static int RunProcess(
        string command,
        string[] arguments,
        Dictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(ResolveCommand(command))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in arguments) psi.ArgumentList.Add(arg);
        if (env is not null)
            foreach (var (key, value) in env)
                psi.Environment[key] = value;

        using var process = Process.Start(psi)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();

        if (!string.IsNullOrWhiteSpace(stdoutTask.Result)) Console.Write(stdoutTask.Result);
        if (!string.IsNullOrWhiteSpace(stderrTask.Result)) Console.Error.Write(stderrTask.Result);

        return process.ExitCode;
    }
}

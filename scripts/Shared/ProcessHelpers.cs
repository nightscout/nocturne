using System.Diagnostics;

public static class ProcessHelpers
{
    /// <summary>Runs a command, streaming output directly. Throws on non-zero exit.</summary>
    public static void Run(string command, string[] arguments, string? workingDir = null)
    {
        var psi = new ProcessStartInfo(command)
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
        var psi = new ProcessStartInfo(command)
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
        var psi = new ProcessStartInfo(command)
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

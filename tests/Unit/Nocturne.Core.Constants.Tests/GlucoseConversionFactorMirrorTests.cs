using System.Globalization;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Nocturne.Core.Constants.Tests;

/// <summary>
/// TypeScript, Rust and the Windhawk mod cannot link <see cref="GlucoseConstants.MgdlPerMmol"/>, so
/// each declares its own copy of the factor. A backend-only change leaves them disagreeing by about
/// 1 part in 7,000 — enough to move a rounded mmol reading. This reads each source and fails on the
/// difference.
/// </summary>
public class GlucoseConversionFactorMirrorTests
{
    public static TheoryData<string, string> Declarations() => new()
    {
        {
            Path.Combine("src", "Web", "packages", "ui", "src", "lib", "glucose.ts"),
            @"export const MGDL_PER_MMOL = ([0-9.]+);"
        },
        {
            Path.Combine("src", "Web", "packages", "desktop", "src-tauri", "src", "glucose_poll.rs"),
            @"pub const MGDL_PER_MMOL: f64 = ([0-9.]+);"
        },
        {
            Path.Combine("src", "Taskbar", "mod.wh.cpp"),
            @"constexpr double kMgdlPerMmol = ([0-9.]+);"
        },
    };

    [Theory]
    [MemberData(nameof(Declarations))]
    public void MirroredFactorMatchesTheBackend(string relativePath, string pattern)
    {
        var path = Path.Combine(RepositoryRoot(), relativePath);
        var match = Regex.Match(File.ReadAllText(path), pattern);

        if (!match.Success)
            throw new InvalidOperationException($"No declaration matching /{pattern}/ in {path}.");

        double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)
            .Should().Be(GlucoseConstants.MgdlPerMmol);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "Web")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"No src/Web directory above {AppContext.BaseDirectory}.");
    }
}

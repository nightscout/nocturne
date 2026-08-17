using System.Globalization;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Nocturne.Core.Constants.Tests;

/// <summary>
/// The frontend converts glucose for display with its own copy of the factor, so a backend-only
/// change leaves the two disagreeing by about 1 part in 7,000 — enough to move a rounded mmol
/// reading. This reads the TypeScript source and fails on the difference.
/// </summary>
public class GlucoseConversionFactorMirrorTests
{
    private static readonly Regex Declaration =
        new(@"export const MGDL_PER_MMOL = ([0-9.]+);", RegexOptions.Compiled);

    [Fact]
    public void TypeScriptUsesTheSameFactorAsTheBackend()
    {
        var path = Path.Combine(RepositoryRoot(), "src", "Web", "packages", "ui", "src", "lib",
            "glucose.ts");
        var match = Declaration.Match(File.ReadAllText(path));

        if (!match.Success)
            throw new InvalidOperationException($"No MGDL_PER_MMOL declaration in {path}.");

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

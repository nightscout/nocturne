using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Nocturne.Core.Constants.Tests;

/// <summary>
/// TypeScript, Rust and the Windhawk mod cannot link the constants that decide how a reading is
/// rendered, so each declares its own copy of the ones it needs: the mg/dL-per-mmol factor, the
/// target range that splits low from in-range from high, and the tile colour per status. Drift is
/// invisible at build time and silent at runtime — a factor off by 1 part in 7,000 moves a rounded
/// mmol reading, and a range or colour off by any amount means two surfaces describe the same
/// reading differently. Each theory reads the declaration out of its source file and fails on the
/// difference. A guard like this is only worth having if it refuses to guess: a declaration that has
/// been renamed or deleted fails rather than passing on nothing, a value left behind on a
/// commented-out line does not stand in for the live one, and two live copies fail rather than
/// letting the first win. The mod is covered three times over — the settings block Windhawk parses,
/// the struct initialiser and the fallback passed at the read site — because any one alone leaves a
/// default that nothing checks.
/// </summary>
public class GlucoseMirrorTests
{
    private static readonly string UiGlucose =
        Path.Combine("src", "Web", "packages", "ui", "src", "lib", "glucose.ts");

    private static readonly string TrayIcon =
        Path.Combine("src", "Web", "packages", "desktop", "src-tauri", "src", "tray.rs");

    private static readonly string GlucosePoll =
        Path.Combine("src", "Web", "packages", "desktop", "src-tauri", "src", "glucose_poll.rs");

    private static readonly string TaskbarMod = Path.Combine("src", "Taskbar", "mod.wh.cpp");

    public static TheoryData<string, string> FactorDeclarations() => new()
    {
        { UiGlucose, @"export const MGDL_PER_MMOL = ([0-9.]+);" },
        { GlucosePoll, @"pub const MGDL_PER_MMOL: f64 = ([0-9.]+);" },
        { TaskbarMod, @"constexpr double kMgdlPerMmol = ([0-9.]+);" },
    };

    [Theory]
    [MemberData(nameof(FactorDeclarations))]
    public void MirroredFactorMatchesTheBackend(string relativePath, string pattern)
    {
        ReadNumber(relativePath, pattern).Should().Be(GlucoseConstants.MgdlPerMmol);
    }

    /// <summary>
    /// The mod's range settings are in the user's display unit — mmol/L by default — at the one
    /// decimal place it renders, so <see cref="ToMmolSetting"/> is as tight as the guard can be
    /// there.
    /// </summary>
    public static TheoryData<string, string, double> TargetRangeDeclarations() => new()
    {
        { TrayIcon, @"const LOW_THRESHOLD_MGDL: f64 = ([0-9.]+);", GlucoseConstants.TargetBottomMgdl },
        { TrayIcon, @"const HIGH_THRESHOLD_MGDL: f64 = ([0-9.]+);", GlucoseConstants.TargetTopMgdl },
        { TaskbarMod, @"- rangeLow: ([0-9.]+)", ToMmolSetting(GlucoseConstants.TargetBottomMgdl) },
        { TaskbarMod, @"- rangeHigh: ([0-9.]+)", ToMmolSetting(GlucoseConstants.TargetTopMgdl) },
        {
            TaskbarMod,
            @"double rangeLow = ([0-9.]+), rangeHigh",
            ToMmolSetting(GlucoseConstants.TargetBottomMgdl)
        },
        {
            TaskbarMod,
            @"rangeLow = [0-9.]+, rangeHigh = ([0-9.]+);",
            ToMmolSetting(GlucoseConstants.TargetTopMgdl)
        },
        {
            TaskbarMod,
            @"rangeLow = getDouble\(L""rangeLow"", ([0-9.]+)\);",
            ToMmolSetting(GlucoseConstants.TargetBottomMgdl)
        },
        {
            TaskbarMod,
            @"rangeHigh = getDouble\(L""rangeHigh"", ([0-9.]+)\);",
            ToMmolSetting(GlucoseConstants.TargetTopMgdl)
        },
    };

    [Theory]
    [MemberData(nameof(TargetRangeDeclarations))]
    public void MirroredTargetRangeMatchesTheBackend(string relativePath, string pattern, double expected)
    {
        ReadNumber(relativePath, pattern).Should().BeApproximately(expected, 1e-9);
    }

    /// <summary>
    /// <see cref="ToMmolSetting"/> describes the mod's range defaults only while the mod itself
    /// defaults to mmol/L. Switching that default to mg/dL leaves every range row above green while
    /// the mod reads 3.9 as a low bound in mg/dL, so the premise is pinned rather than assumed.
    /// </summary>
    public static TheoryData<string, string> DisplayUnitDefaults() => new()
    {
        { @"- unit: (\w+)", "mmol" },
        { @"std::wstring unit = L""([\w/]+)"";", "mmol/L" },
        { @"wcscmp\(unit, L""mgdl""\) == 0\) \? L""mg/dL"" : L""([\w/]+)"";", "mmol/L" },
    };

    [Theory]
    [MemberData(nameof(DisplayUnitDefaults))]
    public void ModRangeDefaultsAreDeclaredInMmol(string pattern, string expected)
    {
        Capture(TaskbarMod, pattern)[0].Should().Be(expected);
    }

    public static TheoryData<string, string, string> PaletteDeclarations() => new()
    {
        { TrayIcon, RustColor("COLOR_IN_RANGE"), GlucoseConstants.StatusPalette.InRange },
        { TrayIcon, RustColor("COLOR_HIGH"), GlucoseConstants.StatusPalette.High },
        { TrayIcon, RustColor("COLOR_LOW"), GlucoseConstants.StatusPalette.Low },
        { TaskbarMod, ModColorFallback("colorInRange"), GlucoseConstants.StatusPalette.InRange },
        { TaskbarMod, ModColorFallback("colorHigh"), GlucoseConstants.StatusPalette.High },
        { TaskbarMod, ModColorFallback("colorLow"), GlucoseConstants.StatusPalette.Low },
        { TaskbarMod, ModColorSetting("colorInRange"), GlucoseConstants.StatusPalette.InRange },
        { TaskbarMod, ModColorSetting("colorHigh"), GlucoseConstants.StatusPalette.High },
        { TaskbarMod, ModColorSetting("colorLow"), GlucoseConstants.StatusPalette.Low },
    };

    [Theory]
    [MemberData(nameof(PaletteDeclarations))]
    public void MirroredPaletteMatchesTheBackend(string relativePath, string pattern, string expected)
    {
        ReadHex(relativePath, pattern).Should().Be(expected.ToUpperInvariant());
    }

    private static string RustColor(string name) =>
        $@"const {name}: \(u8, u8, u8\) = \(0x([0-9A-Fa-f]{{2}}), 0x([0-9A-Fa-f]{{2}}), 0x([0-9A-Fa-f]{{2}})\);";

    private static string ModColorSetting(string key) => $@"- {key}: ""([0-9A-Fa-f]{{6}})""";

    private static string ModColorFallback(string key) =>
        $@"{key} = color\(L""style\.{key}"", RGB\(0x([0-9A-Fa-f]{{2}}), 0x([0-9A-Fa-f]{{2}}), 0x([0-9A-Fa-f]{{2}})\)\);";

    private static double ToMmolSetting(double mgdl) => Math.Round(mgdl / GlucoseConstants.MgdlPerMmol, 1);

    private static double ReadNumber(string relativePath, string pattern) =>
        double.Parse(Capture(relativePath, pattern)[0], CultureInfo.InvariantCulture);

    /// <summary>
    /// The declared colour as upper-case RRGGBB, whether the source writes it as one hex string or as
    /// three byte components.
    /// </summary>
    private static string ReadHex(string relativePath, string pattern) =>
        string.Concat(Capture(relativePath, pattern)).ToUpperInvariant();

    private static string[] Capture(string relativePath, string pattern)
    {
        var path = Path.Combine(RepositoryRoot(), relativePath);
        var text = File.ReadAllText(path);
        var live = Regex.Matches(text, pattern).Where(match => IsLive(text, match.Index)).ToArray();

        if (live.Length != 1)
            throw new InvalidOperationException(
                $"Expected one live declaration matching /{pattern}/ in {path}, found {live.Length}.");

        return live[0].Groups.Cast<Group>().Skip(1).Select(group => group.Value).ToArray();
    }

    /// <summary>
    /// Whether the match is code the surface ships rather than a commented-out line. Only the text
    /// ahead of the match on its own line decides, so a value carrying a trailing note stays live.
    /// </summary>
    private static bool IsLive(string text, int index)
    {
        var prefix = text[(text.LastIndexOf('\n', Math.Max(index - 1, 0)) + 1)..index];

        return !prefix.Contains("//") && !prefix.Contains("/*") && !prefix.TrimStart().StartsWith('#');
    }

    /// <summary>
    /// Anchored to this file's compile-time path, not to the test binary's location: an output
    /// directory outside the checkout would otherwise walk up into a sibling clone and validate that
    /// one's sources without saying so.
    /// </summary>
    private static string RepositoryRoot([CallerFilePath] string testFilePath = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(testFilePath)!);

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "Web")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"No src/Web directory above {testFilePath}.");
    }
}

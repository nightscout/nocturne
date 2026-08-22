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
/// reading differently.
/// <para>
/// Caught: a value edited on one surface and not the others; a declaration renamed, removed, or
/// duplicated; and a superseded copy left behind in a comment or an <c>#if 0</c> block, which is
/// blanked before matching so it cannot answer for the live line. Each search is confined to the
/// region that owns the declaration, so the mod's README can document a default without colliding
/// with the settings block that sets it.
/// </para>
/// <para>
/// Not caught: this reads text, not programs. A declaration reachable only through an inactive
/// <c>#else</c> branch still reads as live; a value assembled at runtime from somewhere else is
/// invisible; and declarations that agree are not proof that each surface uses the one it declares.
/// A surface whose copy lives somewhere no row names is not covered at all.
/// </para>
/// </summary>
public class GlucoseMirrorTests
{
    private const string UiGlucose = "@nocturne/ui glucose.ts";
    private const string TrayIcon = "desktop tray.rs";
    private const string GlucosePoll = "desktop glucose_poll.rs";
    private const string ModFile = "mod.wh.cpp";
    private const string ModSettings = "mod.wh.cpp settings block";
    private const string ModStruct = "mod.wh.cpp Settings struct";
    private const string ModLoadSettings = "mod.wh.cpp LoadSettings";

    private enum Syntax
    {
        Code,
        Yaml,
    }

    private sealed record Source(string RelativePath, Syntax Syntax, string? Open = null, string? Close = null);

    private static readonly string ModPath = Path.Combine("src", "Taskbar", "mod.wh.cpp");

    private static readonly Dictionary<string, Source> Sources = new()
    {
        [UiGlucose] = new(
            Path.Combine("src", "Web", "packages", "ui", "src", "lib", "glucose.ts"), Syntax.Code),
        [TrayIcon] = new(
            Path.Combine("src", "Web", "packages", "desktop", "src-tauri", "src", "tray.rs"), Syntax.Code),
        [GlucosePoll] = new(
            Path.Combine("src", "Web", "packages", "desktop", "src-tauri", "src", "glucose_poll.rs"), Syntax.Code),
        [ModFile] = new(ModPath, Syntax.Code),
        [ModSettings] = new(
            ModPath, Syntax.Yaml, "// ==WindhawkModSettings==", "// ==/WindhawkModSettings=="),
        [ModStruct] = new(ModPath, Syntax.Code, "struct Settings {", "\n};"),
        [ModLoadSettings] = new(ModPath, Syntax.Code, "void LoadSettings() {", "\n}"),
    };

    public static TheoryData<string, string> FactorDeclarations() => new()
    {
        { UiGlucose, @"export const MGDL_PER_MMOL = ([0-9.]+);" },
        { GlucosePoll, @"pub const MGDL_PER_MMOL: f64 = ([0-9.]+);" },
        { ModFile, @"constexpr double kMgdlPerMmol = ([0-9.]+);" },
    };

    [Theory]
    [MemberData(nameof(FactorDeclarations))]
    public void MirroredFactorMatchesTheBackend(string source, string pattern)
    {
        ReadNumber(source, pattern).Should().Be(GlucoseConstants.MgdlPerMmol);
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
        { ModSettings, @"- rangeLow: ([0-9.]+)", ToMmolSetting(GlucoseConstants.TargetBottomMgdl) },
        { ModSettings, @"- rangeHigh: ([0-9.]+)", ToMmolSetting(GlucoseConstants.TargetTopMgdl) },
        {
            ModStruct,
            @"double rangeLow = ([0-9.]+), rangeHigh",
            ToMmolSetting(GlucoseConstants.TargetBottomMgdl)
        },
        {
            ModStruct,
            @"rangeLow = [0-9.]+, rangeHigh = ([0-9.]+);",
            ToMmolSetting(GlucoseConstants.TargetTopMgdl)
        },
        {
            ModLoadSettings,
            @"rangeLow = getDouble\(L""rangeLow"", ([0-9.]+)\);",
            ToMmolSetting(GlucoseConstants.TargetBottomMgdl)
        },
        {
            ModLoadSettings,
            @"rangeHigh = getDouble\(L""rangeHigh"", ([0-9.]+)\);",
            ToMmolSetting(GlucoseConstants.TargetTopMgdl)
        },
    };

    [Theory]
    [MemberData(nameof(TargetRangeDeclarations))]
    public void MirroredTargetRangeMatchesTheBackend(string source, string pattern, double expected)
    {
        ReadNumber(source, pattern).Should().BeApproximately(expected, 1e-9);
    }

    /// <summary>
    /// <see cref="ToMmolSetting"/> describes the mod's range defaults only while the mod itself
    /// defaults to mmol/L. Switching that default to mg/dL leaves every range row above green while
    /// the mod reads 3.9 as a low bound in mg/dL, so the premise is pinned rather than assumed.
    /// </summary>
    public static TheoryData<string, string, string> DisplayUnitDefaults() => new()
    {
        { ModSettings, @"- unit: (\w+)", "mmol" },
        { ModStruct, @"std::wstring unit = L""([\w/]+)"";", "mmol/L" },
        { ModLoadSettings, @"wcscmp\(unit, L""mgdl""\) == 0\) \? L""mg/dL"" : L""([\w/]+)"";", "mmol/L" },
    };

    [Theory]
    [MemberData(nameof(DisplayUnitDefaults))]
    public void ModRangeDefaultsAreDeclaredInMmol(string source, string pattern, string expected)
    {
        Capture(source, pattern)[0].Should().Be(expected);
    }

    public static TheoryData<string, string, string> PaletteDeclarations() => new()
    {
        { TrayIcon, RustColor("COLOR_IN_RANGE"), GlucoseConstants.StatusPalette.InRange },
        { TrayIcon, RustColor("COLOR_HIGH"), GlucoseConstants.StatusPalette.High },
        { TrayIcon, RustColor("COLOR_LOW"), GlucoseConstants.StatusPalette.Low },
        { ModLoadSettings, ModColorFallback("colorInRange"), GlucoseConstants.StatusPalette.InRange },
        { ModLoadSettings, ModColorFallback("colorHigh"), GlucoseConstants.StatusPalette.High },
        { ModLoadSettings, ModColorFallback("colorLow"), GlucoseConstants.StatusPalette.Low },
        { ModSettings, ModColorSetting("colorInRange"), GlucoseConstants.StatusPalette.InRange },
        { ModSettings, ModColorSetting("colorHigh"), GlucoseConstants.StatusPalette.High },
        { ModSettings, ModColorSetting("colorLow"), GlucoseConstants.StatusPalette.Low },
    };

    [Theory]
    [MemberData(nameof(PaletteDeclarations))]
    public void MirroredPaletteMatchesTheBackend(string source, string pattern, string expected)
    {
        ReadHex(source, pattern).Should().Be(expected.ToUpperInvariant());
    }

    private static string RustColor(string name) =>
        $@"const {name}: \(u8, u8, u8\) = \(0x([0-9A-Fa-f]{{2}}), 0x([0-9A-Fa-f]{{2}}), 0x([0-9A-Fa-f]{{2}})\);";

    private static string ModColorSetting(string key) => $@"- {key}: ""([0-9A-Fa-f]{{6}})""";

    private static string ModColorFallback(string key) =>
        $@"{key} = color\(L""style\.{key}"", RGB\(0x([0-9A-Fa-f]{{2}}), 0x([0-9A-Fa-f]{{2}}), 0x([0-9A-Fa-f]{{2}})\)\);";

    private static double ToMmolSetting(double mgdl) => Math.Round(mgdl / GlucoseConstants.MgdlPerMmol, 1);

    private static double ReadNumber(string source, string pattern) =>
        double.Parse(Capture(source, pattern)[0], CultureInfo.InvariantCulture);

    /// <summary>
    /// The declared colour as upper-case RRGGBB, whether the source writes it as one hex string or as
    /// three byte components.
    /// </summary>
    private static string ReadHex(string source, string pattern) =>
        string.Concat(Capture(source, pattern)).ToUpperInvariant();

    private static string[] Capture(string source, string pattern)
    {
        var declaration = Sources[source];
        var path = Path.Combine(RepositoryRoot(), declaration.RelativePath);
        var matches = Regex.Matches(LiveText(File.ReadAllText(path), declaration), pattern);

        if (matches.Count != 1)
            throw new InvalidOperationException(
                $"Expected one live declaration matching /{pattern}/ in {source} ({path}), found {matches.Count}.");

        return matches[0].Groups.Cast<Group>().Skip(1).Select(group => group.Value).ToArray();
    }

    /// <summary>
    /// The region that owns the declaration, with everything that does not compile — comments, and
    /// <c>#if 0</c> blocks in code — replaced by blanks. Narrowing to the region is what lets code
    /// regions treat a block comment as dead: the mod's settings block is itself one long block
    /// comment, and it is only ever searched as YAML.
    /// </summary>
    private static string LiveText(string text, Source source)
    {
        var region = text;

        if (source.Open is not null)
        {
            var start = region.IndexOf(source.Open, StringComparison.Ordinal);

            if (start < 0)
                throw new InvalidOperationException($"No region opening with '{source.Open}'.");

            start += source.Open.Length;
            var end = region.IndexOf(source.Close!, start, StringComparison.Ordinal);

            if (end < 0)
                throw new InvalidOperationException($"No region closing with '{source.Close}'.");

            region = region[start..end];
        }

        return source.Syntax == Syntax.Yaml ? BlankYamlComments(region) : BlankInactiveCode(region);
    }

    private static string BlankYamlComments(string text)
    {
        var characters = text.ToCharArray();
        var commented = false;

        for (var index = 0; index < characters.Length; index++)
        {
            if (characters[index] == '\n')
                commented = false;
            else if (characters[index] == '#')
                commented = true;

            if (commented)
                characters[index] = ' ';
        }

        return new string(characters);
    }

    private static string BlankInactiveCode(string text) => BlankDisabledBlocks(BlankCodeComments(text));

    private static string BlankCodeComments(string text)
    {
        var characters = text.ToCharArray();
        var index = 0;

        while (index < characters.Length)
        {
            if (Starts(characters, index, "//"))
            {
                while (index < characters.Length && characters[index] != '\n')
                    characters[index++] = ' ';
            }
            else if (Starts(characters, index, "/*"))
            {
                while (index < characters.Length && !Starts(characters, index, "*/"))
                {
                    if (characters[index] != '\n')
                        characters[index] = ' ';

                    index++;
                }

                for (var closer = 0; closer < 2 && index < characters.Length; closer++)
                    characters[index++] = ' ';
            }
            else
            {
                index++;
            }
        }

        return new string(characters);
    }

    private static string BlankDisabledBlocks(string text)
    {
        var lines = text.Split('\n');
        var depth = 0;

        for (var index = 0; index < lines.Length; index++)
        {
            var directive = lines[index].TrimStart();

            if (depth == 0)
            {
                if (!directive.StartsWith("#if 0", StringComparison.Ordinal))
                    continue;

                depth = 1;
            }
            else if (directive.StartsWith("#if", StringComparison.Ordinal))
            {
                depth++;
            }
            else if (directive.StartsWith("#endif", StringComparison.Ordinal))
            {
                depth--;
            }

            lines[index] = new string(' ', lines[index].Length);
        }

        return string.Join('\n', lines);
    }

    private static bool Starts(char[] characters, int index, string token) =>
        index + token.Length <= characters.Length
        && new ReadOnlySpan<char>(characters, index, token.Length).SequenceEqual(token);

    /// <summary>
    /// Two anchors that have to agree: the tree this file was compiled from, and the tree the test
    /// binary is running out of. Each is silently wrong on its own — a binary run from outside the
    /// checkout resolves the tree it sits in, and a tree copied without rebuilding resolves the one
    /// it came from — and either way the guard passes having read sources nobody edited.
    /// </summary>
    private static string RepositoryRoot([CallerFilePath] string testFilePath = "")
    {
        var compiledFrom = SourceTreeAbove(Path.GetDirectoryName(testFilePath)!);
        var runningIn = SourceTreeAbove(AppContext.BaseDirectory);

        if (!string.Equals(compiledFrom, runningIn, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Compiled from {compiledFrom} but running out of {runningIn}; rebuild in place. "
                + "Reading one tree while another ships is the failure this guard exists to prevent.");

        return compiledFrom;
    }

    private static string SourceTreeAbove(string start)
    {
        var directory = new DirectoryInfo(start);

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "Web")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"No src/Web directory above {start}.");
    }
}

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
/// Caught, for the declarations the rows below name: a value edited on one surface and not the
/// others; a declaration renamed, deleted, or duplicated, each of which fails rather than passing on
/// nothing; and a superseded copy left behind in a comment, which is blanked before matching so it
/// cannot answer for the live line.
/// </para>
/// <para>
/// Not caught: the mod's compiled-in C++ fallbacks — the <c>Settings</c> struct initialisers and the
/// defaults passed at the <c>LoadSettings</c> read sites — are not guarded. Those apply only when a
/// Windhawk setting is absent, and enforcing them means parsing C++ rather than reading it; three
/// attempts to bound them by region left paths where this passed while the value had moved, which is
/// worse than an unguarded copy. What the mod ships to a user who has not overridden anything is the
/// settings block, and that is guarded. Beyond that: this reads text, not programs, so a value
/// assembled at runtime is invisible, and declarations that agree are not proof a surface uses the
/// one it declares.
/// </para>
/// </summary>
public class GlucoseMirrorTests
{
    private const string UiGlucose = "@nocturne/ui glucose.ts";
    private const string TrayIcon = "desktop tray.rs";
    private const string GlucosePoll = "desktop glucose_poll.rs";
    private const string ModSettings = "mod.wh.cpp settings block";

    private enum Syntax
    {
        Code,
        Yaml,
    }

    private sealed record Source(string RelativePath, Syntax Syntax, string? Open = null, string? Close = null);

    private static readonly Dictionary<string, Source> Sources = new()
    {
        [UiGlucose] = new(
            Path.Combine("src", "Web", "packages", "ui", "src", "lib", "glucose.ts"), Syntax.Code),
        [TrayIcon] = new(
            Path.Combine("src", "Web", "packages", "desktop", "src-tauri", "src", "tray.rs"), Syntax.Code),
        [GlucosePoll] = new(
            Path.Combine("src", "Web", "packages", "desktop", "src-tauri", "src", "glucose_poll.rs"), Syntax.Code),
        [ModSettings] = new(
            Path.Combine("src", "Taskbar", "mod.wh.cpp"),
            Syntax.Yaml,
            "// ==WindhawkModSettings==",
            "// ==/WindhawkModSettings=="),
    };

    public static TheoryData<string, string> FactorDeclarations() => new()
    {
        { UiGlucose, @"export const MGDL_PER_MMOL = ([0-9.]+);" },
        { GlucosePoll, @"pub const MGDL_PER_MMOL: f64 = ([0-9.]+);" },
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
    };

    [Theory]
    [MemberData(nameof(TargetRangeDeclarations))]
    public void MirroredTargetRangeMatchesTheBackend(string source, string pattern, double expected)
    {
        ReadNumber(source, pattern).Should().BeApproximately(expected, 1e-9);
    }

    /// <summary>
    /// <see cref="ToMmolSetting"/> describes the mod's range defaults only while the mod itself
    /// defaults to mmol/L. Switching that default to mg/dL leaves both range rows above green while
    /// the mod reads 3.9 as a low bound in mg/dL, so the premise is pinned rather than assumed.
    /// </summary>
    public static TheoryData<string, string, string> DisplayUnitDefaults() => new()
    {
        { ModSettings, @"- unit: (\w+)", "mmol" },
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

    /// <summary>
    /// A leading <c>#</c> is admitted so that writing one is reported as the wrong value it is: the
    /// mod parses these with <c>swscanf(L"%2x%2x%2x")</c>, which rejects <c>#RRGGBB</c> outright and
    /// silently keeps its own default.
    /// </summary>
    private static string ModColorSetting(string key) => $@"- {key}: ""(#?[0-9A-Fa-f]{{6}})""";

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

    private static string LiveText(string text, Source source) => source.Syntax switch
    {
        Syntax.Yaml => BlankYamlComments(SettingsBlock(text, source)),
        _ => BlankCodeComments(text),
    };

    /// <summary>
    /// The mod's settings block, located by its delimiter lines rather than by the first mention of
    /// one: the README above it is prose that may quote a delimiter, and a region that quietly starts
    /// somewhere else would guard the wrong text.
    /// </summary>
    private static string SettingsBlock(string text, Source source)
    {
        var open = SoleDelimiterLine(text, source.Open!);
        var close = SoleDelimiterLine(text, source.Close!);

        if (close <= open)
            throw new InvalidOperationException($"'{source.Close}' precedes '{source.Open}'.");

        return text[(open + source.Open!.Length)..close];
    }

    private static int SoleDelimiterLine(string text, string delimiter)
    {
        var matches = Regex.Matches(text, $@"(?m)^{Regex.Escape(delimiter)}[ \t]*\r?$");

        if (matches.Count != 1)
            throw new InvalidOperationException(
                $"Expected one line reading '{delimiter}', found {matches.Count}.");

        return matches[0].Index;
    }

    /// <summary>
    /// The same text with YAML comments blanked. A <c>#</c> inside a quoted value is part of the
    /// value, so the row is still read and reported as malformed rather than missing.
    /// </summary>
    private static string BlankYamlComments(string text)
    {
        var characters = text.ToCharArray();
        var quoted = false;
        var commented = false;

        for (var index = 0; index < characters.Length; index++)
        {
            if (characters[index] == '\n')
            {
                quoted = false;
                commented = false;
                continue;
            }

            if (!commented && characters[index] == '"')
                quoted = !quoted;
            else if (!quoted && characters[index] == '#')
                commented = true;

            if (commented)
                characters[index] = ' ';
        }

        return new string(characters);
    }

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
        var directory = new DirectoryInfo(FinalTarget(start));

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "Web")))
                return FinalTarget(directory.FullName);

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"No src/Web directory above {start}.");
    }

    /// <summary>
    /// Where a junction or symlink ultimately points, so two anchors that reach the same tree by
    /// different routes compare equal instead of reading as a stale build.
    /// </summary>
    private static string FinalTarget(string path)
    {
        var resolved = Directory.Exists(path)
            ? Directory.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path
            : path;

        return resolved.TrimEnd(Path.DirectorySeparatorChar);
    }
}

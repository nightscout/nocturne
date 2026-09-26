using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
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
/// TypeScript and Rust are read by regex, with comments blanked so a superseded copy left behind in
/// one cannot answer for the live line. The mod is read as data: <c>src/Taskbar/settings-defaults.json</c>
/// holds its unit, range and colour defaults, and both surfaces that ship those values — the
/// <c>==WindhawkModSettings==</c> block Windhawk hands to a user who has overridden nothing, and the
/// <c>==SettingsDefaults==</c> block of <c>constexpr</c>s the mod falls back on for a value Windhawk
/// cannot supply or the mod cannot parse — are held against that file line for line. Nothing here
/// lexes C++.
/// </para>
/// <para>
/// Caught, for the declarations the rows below name: a value edited on one surface and not the
/// others; a declaration renamed, deleted, or duplicated, each of which fails rather than passing on
/// nothing; and a mod default that ships in one of the mod's two blocks but not the other.
/// </para>
/// <para>
/// Not caught: the mod's compiled-in constants that are not settings defaults, which
/// <c>settings-defaults.json</c> does not cover — its own copy of the factor
/// (<c>mod.wh.cpp</c>'s <c>kMgdlPerMmol</c>) and the theme-derived text colours. Beyond that: this
/// reads text, not programs, so a value assembled at runtime is invisible, and declarations that
/// agree are not proof a surface uses the one it declares —
/// <see cref="ModReferencesEveryCompiledDefault"/> only establishes that each <c>constexpr</c> is
/// named somewhere besides its own declaration, not that it is named at the read site it belongs to.
/// </para>
/// </summary>
public class GlucoseMirrorTests
{
    private const string UiGlucose = "@nocturne/ui glucose.ts";
    private const string TrayIcon = "desktop tray.rs";
    private const string GlucosePoll = "desktop glucose_poll.rs";

    private static readonly Dictionary<string, string> Sources = new()
    {
        [UiGlucose] = Path.Combine("src", "Web", "packages", "ui", "src", "lib", "glucose.ts"),
        [TrayIcon] = Path.Combine("src", "Web", "packages", "desktop", "src-tauri", "src", "tray.rs"),
        [GlucosePoll] =
            Path.Combine("src", "Web", "packages", "desktop", "src-tauri", "src", "glucose_poll.rs"),
    };

    private static readonly string ModDefaultsPath = Path.Combine("src", "Taskbar", "settings-defaults.json");
    private static readonly string ModSourcePath = Path.Combine("src", "Taskbar", "mod.wh.cpp");

    private const string SettingsOpen = "// ==WindhawkModSettings==";
    private const string SettingsClose = "// ==/WindhawkModSettings==";
    private const string DefaultsOpen = "// ==SettingsDefaults==";
    private const string DefaultsClose = "// ==/SettingsDefaults==";

    /// <summary>How a default is written on each of the mod's two surfaces.</summary>
    private enum Shape
    {
        Option,
        Number,
        Color,
    }

    private sealed record ModDefault(string Key, Shape Shape, string Constant);

    private static readonly ModDefault[] ModDefaults =
    [
        new("unit", Shape.Option, "kDefaultUnit"),
        new("rangeLow", Shape.Number, "kDefaultRangeLow"),
        new("rangeHigh", Shape.Number, "kDefaultRangeHigh"),
        new("colorInRange", Shape.Color, "kDefaultColorInRange"),
        new("colorHigh", Shape.Color, "kDefaultColorHigh"),
        new("colorLow", Shape.Color, "kDefaultColorLow"),
        new("colorPredicted", Shape.Color, "kDefaultColorPredicted"),
        new("textColor", Shape.Color, "kDefaultTextColor"),
    ];

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

    public static TheoryData<string, string, double> TargetRangeDeclarations() => new()
    {
        { TrayIcon, @"const LOW_THRESHOLD_MGDL: f64 = ([0-9.]+);", GlucoseConstants.TargetBottomMgdl },
        { TrayIcon, @"const HIGH_THRESHOLD_MGDL: f64 = ([0-9.]+);", GlucoseConstants.TargetTopMgdl },
    };

    [Theory]
    [MemberData(nameof(TargetRangeDeclarations))]
    public void MirroredTargetRangeMatchesTheBackend(string source, string pattern, double expected)
    {
        ReadNumber(source, pattern).Should().BeApproximately(expected, 1e-9);
    }

    public static TheoryData<string, string, string> PaletteDeclarations() => new()
    {
        { TrayIcon, RustColor("COLOR_IN_RANGE"), GlucoseConstants.StatusPalette.InRange },
        { TrayIcon, RustColor("COLOR_HIGH"), GlucoseConstants.StatusPalette.High },
        { TrayIcon, RustColor("COLOR_LOW"), GlucoseConstants.StatusPalette.Low },
    };

    [Theory]
    [MemberData(nameof(PaletteDeclarations))]
    public void MirroredPaletteMatchesTheBackend(string source, string pattern, string expected)
    {
        ReadHex(source, pattern).Should().Be(expected.ToUpperInvariant());
    }

    /// <summary>
    /// The mod's range defaults are in the user's display unit — mmol/L by default — at the one
    /// decimal place it renders, so <see cref="ToMmolSetting"/> is as tight as the guard can be
    /// there.
    /// </summary>
    public static TheoryData<string, double> ModTargetRangeDefaults() => new()
    {
        { "rangeLow", ToMmolSetting(GlucoseConstants.TargetBottomMgdl) },
        { "rangeHigh", ToMmolSetting(GlucoseConstants.TargetTopMgdl) },
    };

    [Theory]
    [MemberData(nameof(ModTargetRangeDefaults))]
    public void ModTargetRangeDefaultMatchesTheBackend(string key, double expected)
    {
        double.Parse(ModDefaultValue(key), CultureInfo.InvariantCulture)
            .Should().BeApproximately(expected, 1e-9);
    }

    /// <summary>
    /// <see cref="ToMmolSetting"/> describes the mod's range defaults only while the mod itself
    /// defaults to mmol/L. Switching that default to mg/dL leaves both range rows above green while
    /// the mod reads 3.9 as a low bound in mg/dL, so the premise is pinned rather than assumed.
    /// </summary>
    [Fact]
    public void ModRangeDefaultsAreDeclaredInMmol()
    {
        ModDefaultValue("unit").Should().Be("mmol");
    }

    public static TheoryData<string, string> ModPaletteDefaults() => new()
    {
        { "colorInRange", GlucoseConstants.StatusPalette.InRange },
        { "colorHigh", GlucoseConstants.StatusPalette.High },
        { "colorLow", GlucoseConstants.StatusPalette.Low },
    };

    [Theory]
    [MemberData(nameof(ModPaletteDefaults))]
    public void ModPaletteDefaultMatchesTheBackend(string key, string expected)
    {
        ModDefaultValue(key).Should().Be(expected.ToUpperInvariant());
    }

    /// <summary>
    /// What Windhawk hands out for a setting the user has not overridden, and therefore what the mod
    /// ships with.
    /// </summary>
    [Fact]
    public void ModSettingsBlockDeclaresTheDataFileDefaults()
    {
        var block = Region(ModSource(), SettingsOpen, SettingsClose);
        var declared = DeclaredModDefaults();

        foreach (var mod in ModDefaults)
            SettingsRow(block, mod.Key).Should()
                .Be(SettingsValue(mod, declared[mod.Key]), $"{mod.Key} is declared in {ModDefaultsPath}");
    }

    /// <summary>
    /// The mod is distributed as one file pasted into Windhawk's editor, so it cannot include a
    /// generated header: the constants have to live in <c>mod.wh.cpp</c>, and the block that carries
    /// them is rendered from the data file here so drift is a failure rather than a compile that
    /// silently ships another value. A red run prints the block to paste.
    /// </summary>
    [Fact]
    public void ModCompiledDefaultsMatchTheDataFile()
    {
        var declared = DeclaredModDefaults();

        Lines(Region(ModSource(), DefaultsOpen, DefaultsClose))
            .Should().Equal(ModDefaults.Select(mod => Declaration(mod, declared[mod.Key])));
    }

    /// <summary>
    /// A constant named only by its own declaration is text the mod does not read, which is how a
    /// generated block goes stale while every row above stays green. This says each is named
    /// somewhere else in the file; it does not say where.
    /// </summary>
    [Fact]
    public void ModReferencesEveryCompiledDefault()
    {
        var source = ModSource();

        foreach (var mod in ModDefaults)
            Regex.Matches(source, $@"\b{Regex.Escape(mod.Constant)}\b").Count
                .Should().BeGreaterThan(1, $"{mod.Constant} is declared but never read");
    }

    private static string RustColor(string name) =>
        $@"const {name}: \(u8, u8, u8\) = \(0x([0-9A-Fa-f]{{2}}), 0x([0-9A-Fa-f]{{2}}), 0x([0-9A-Fa-f]{{2}})\);";

    private static double ToMmolSetting(double mgdl) => Math.Round(mgdl / GlucoseConstants.MgdlPerMmol, 1);

    private static string ModDefaultValue(string key) => DeclaredModDefaults()[key];

    /// <summary>
    /// The mod's settings defaults, keyed by setting name. Every value is the literal text that
    /// ships on both of the mod's surfaces, so the file states what each of them must read rather
    /// than a number a formatter has to render back the same way twice.
    /// </summary>
    private static Dictionary<string, string> DeclaredModDefaults()
    {
        var path = Path.Combine(RepositoryRoot(), ModDefaultsPath);
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var declared = document.RootElement.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Value.GetString()
                        ?? throw new InvalidOperationException($"{property.Name} in {path} is not a string."));

        if (!declared.Keys.Order().SequenceEqual(ModDefaults.Select(mod => mod.Key).Order()))
            throw new InvalidOperationException(
                $"{path} declares [{string.Join(", ", declared.Keys)}]; expected "
                + $"[{string.Join(", ", ModDefaults.Select(mod => mod.Key))}].");

        foreach (var mod in ModDefaults.Where(mod => mod.Shape == Shape.Color))
            if (!Regex.IsMatch(declared[mod.Key], "^[0-9A-F]{6}$"))
                throw new InvalidOperationException(
                    $"{mod.Key} in {path} reads '{declared[mod.Key]}', which is not upper-case RRGGBB.");

        return declared;
    }

    private static string ModSource() => File.ReadAllText(Path.Combine(RepositoryRoot(), ModSourcePath));

    /// <summary>
    /// The value text of a <c>- key: value</c> row, at whatever nesting the setting sits at. A row
    /// commented out with a leading <c>#</c> is not a row, so a superseded copy cannot answer for the
    /// live one, and a trailing comment is part of the value and reported as the wrong text it is.
    /// </summary>
    private static string SettingsRow(string block, string key)
    {
        var matches = Regex.Matches(block, $@"(?m)^[ \t]*- {Regex.Escape(key)}: (.*)$");

        if (matches.Count != 1)
            throw new InvalidOperationException(
                $"Expected one '- {key}:' row in the mod's settings block, found {matches.Count}.");

        return matches[0].Groups[1].Value.TrimEnd();
    }

    /// <summary>
    /// A colour is quoted so YAML keeps it a string; an all-digit hex would otherwise parse as a
    /// number, which the mod's <c>swscanf(L"%2x%2x%2x")</c> read cannot recover.
    /// </summary>
    private static string SettingsValue(ModDefault mod, string value) =>
        mod.Shape == Shape.Color ? $"\"{value}\"" : value;

    private static string Declaration(ModDefault mod, string value) => mod.Shape switch
    {
        Shape.Option => $"constexpr PCWSTR {mod.Constant} = L\"{value}\";",
        Shape.Number => $"constexpr double {mod.Constant} = {value};",
        _ => $"constexpr COLORREF {mod.Constant} = "
             + $"RGB(0x{value[..2]}, 0x{value[2..4]}, 0x{value[4..]});",
    };

    private static string[] Lines(string text) =>
        text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();

    /// <summary>
    /// The text between two delimiter lines, each located as a whole line rather than by its first
    /// mention: the mod's README above them is prose that may quote a delimiter, and a region that
    /// quietly starts somewhere else would guard the wrong text.
    /// </summary>
    private static string Region(string text, string open, string close)
    {
        var start = SoleDelimiterLine(text, open);
        var end = SoleDelimiterLine(text, close);

        if (end <= start)
            throw new InvalidOperationException($"'{close}' precedes '{open}'.");

        return text[(start + open.Length)..end];
    }

    private static int SoleDelimiterLine(string text, string delimiter)
    {
        var matches = Regex.Matches(text, $@"(?m)^{Regex.Escape(delimiter)}[ \t]*\r?$");

        if (matches.Count != 1)
            throw new InvalidOperationException(
                $"Expected one line reading '{delimiter}', found {matches.Count}.");

        return matches[0].Index;
    }

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
        var path = Path.Combine(RepositoryRoot(), Sources[source]);
        var matches = Regex.Matches(BlankCodeComments(File.ReadAllText(path)), pattern);

        if (matches.Count != 1)
            throw new InvalidOperationException(
                $"Expected one live declaration matching /{pattern}/ in {source} ({path}), found {matches.Count}.");

        return matches[0].Groups.Cast<Group>().Skip(1).Select(group => group.Value).ToArray();
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

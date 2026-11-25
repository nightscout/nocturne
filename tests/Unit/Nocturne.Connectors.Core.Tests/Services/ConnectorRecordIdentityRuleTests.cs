using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Nocturne.Connectors.Core.Tests.Services;

/// <summary>
/// A connector leaves a record's id to the publish, which stamps one only where none is set, and
/// builds its stored keys through <c>ConnectorLegacyIds</c>, so the prefix comes from its registration
/// rather than a literal spelled again at each site.
/// </summary>
/// <remarks>
/// A source scan over every file of every connector project but Core. It cannot see an id minted
/// another way (<c>Guid.NewGuid</c>, a helper in another assembly), a prefix held in a constant or
/// assembled by concatenation (<c>"glooko" + "_"</c>), a key that carries no prefix at all, or a
/// suffix appended to a key already built.
/// </remarks>
public partial class ConnectorRecordIdentityRuleTests
{
    /// <summary>
    /// Ids a connector mints itself, each set before the publish because another record of the same
    /// batch refers to it, or because the type is one the publish does not stamp.
    /// </summary>
    private static readonly Dictionary<string, (int Count, string Reason)> MintsIds = new()
    {
        ["Nocturne.Connectors.CareLink/Mappers/CareLinkDeviceStatusMapper.cs"] = (1,
            "a device status's id is the legacy key its decomposed snapshots are stored under; CareLink " +
            "mints one per snapshot and Tandem sends none, so the publish leaves device status ids alone"),
        ["Nocturne.Connectors.Glooko/Mappers/GlookoV4TreatmentMapper.cs"] = (2,
            "a decomposition batch's id is the correlation id of the bolus and carb intake it groups"),
        ["Nocturne.Connectors.MyLife/Mappers/Handlers/BolusHandler.cs"] = (1,
            "a decomposition batch's id is the correlation id of the bolus, carb intake and calculation it groups"),
        ["Nocturne.Connectors.Tidepool/Mappers/TidepoolV4TreatmentMapper.cs"] = (1,
            "a decomposition batch's id is the correlation id of the bolus and carb intake it groups"),
        ["Nocturne.Connectors.Tandem/Mappers/TandemBolusMapper.cs"] = (1,
            "the correlation id of a bolus with its carb intake and calculation"),
    };

    [GeneratedRegex(@"\bGuid\.CreateVersion7\s*\(")]
    private static partial Regex MintsAnId();

    [GeneratedRegex(@"\bLegacyIdPrefix\s*=\s*""(?<prefix>[^""]+)""")]
    private static partial Regex DeclaresAPrefix();

    [Fact]
    public void NoConnectorMintsAnIdOrSpellsAKeyPrefix()
    {
        var prefixes = DeclaredPrefixes();

        var offences = Sources()
            .SelectMany(file => OffencesIn(Relative(file), File.ReadAllText(file), prefixes))
            .ToList();

        offences.Should().BeEmpty(
            "the publish stamps a record's id, and ConnectorLegacyIds builds its key from the registered prefix");
    }

    [Fact]
    public void EveryAllowedMintStillExists()
    {
        var texts = Sources().ToDictionary(Relative, File.ReadAllText);

        foreach (var (file, (count, _)) in MintsIds)
        {
            texts.Should().ContainKey(file, "an allowlist entry names a file that no longer exists");
            MintsAnId().Matches(texts[file]).Count.Should().Be(count,
                $"{file} mints exactly the ids its allowlist entry gives a reason for");
        }
    }

    [Fact]
    public void TheScanFlagsAMintedIdAndAHandBuiltPrefix()
    {
        const string source = """
            var a = new Bolus { Id = Guid.CreateVersion7(), LegacyId = $"tandem_bolus_{id}" };
            var b = "glooko_" + key;
            var c = LegacyIds.Of("bolus", id);
            var d = "tandemish";
            """;

        OffencesIn("X.cs", source, ["tandem", "glooko"]).Select(o => o.Reason).Should().Equal(
            "mints an id: Guid.CreateVersion7(",
            "spells a key prefix: \"tandem_",
            "spells a key prefix: \"glooko_");
    }

    [Fact]
    public void TheScanSeesTheConnectorsAndTheirPrefixes()
    {
        Sources().Should().HaveCountGreaterThanOrEqualTo(100,
            "finding almost no connector sources means the scan lost the source tree rather than that the tree is clean");
        DeclaredPrefixes().Should().Contain(["carelink", "dexcom", "glooko", "mylife", "tandem", "tidepool", "twiist"]);
    }

    private static IEnumerable<(string File, string Reason)> OffencesIn(
        string file, string text, IReadOnlyCollection<string> prefixes)
    {
        var allowed = MintsIds.TryGetValue(file, out var entry) ? entry.Count : 0;
        var mints = MintsAnId().Matches(text);
        if (mints.Count > allowed)
            foreach (var _ in mints.Skip(allowed))
                yield return (file, "mints an id: Guid.CreateVersion7(");

        var literal = new Regex($@"""(?<prefix>{string.Join('|', prefixes.Select(Regex.Escape))})_");
        foreach (Match match in literal.Matches(text))
            yield return (file, $"spells a key prefix: \"{match.Groups["prefix"].Value}_");
    }

    private static List<string> DeclaredPrefixes() =>
        [.. Sources().SelectMany(file => DeclaresAPrefix().Matches(File.ReadAllText(file)))
            .Select(match => match.Groups["prefix"].Value)
            .Distinct()];

    private static string Connectors() => Path.Combine(RepositoryTree.Root(), "src", "Connectors");

    private static string Relative(string file) =>
        Path.GetRelativePath(Connectors(), file).Replace(Path.DirectorySeparatorChar, '/');

    private static List<string> Sources()
    {
        var separator = Path.DirectorySeparatorChar;

        return [.. Directory.EnumerateFiles(Connectors(), "*.cs", SearchOption.AllDirectories)
            .Where(file => !Relative(file).StartsWith("Nocturne.Connectors.Core/", StringComparison.Ordinal)
                           && !file.Contains($"{separator}obj{separator}")
                           && !file.Contains($"{separator}bin{separator}"))];
    }
}

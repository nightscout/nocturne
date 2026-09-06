using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Nocturne.Connectors.Core.Extensions;
using Xunit;

namespace Nocturne.Connectors.Core.Tests.Extensions;

/// <summary>
/// The frontend labels every connector property from a hand-written table, and derives its own key
/// type from that table's keys — so TypeScript cannot notice a key the backend has and the table
/// does not. This reads both sides and fails on the difference; without it a new property key
/// renders with no description under the wrong heading.
/// </summary>
public class ConnectorPropertyMetaMirrorTests
{
    private static readonly Regex MetaEntry = new(@"^  (\w+): \{$", RegexOptions.Compiled);

    [Fact]
    public void TypeScriptTableCoversExactlyTheConnectorPropertyKeys()
    {
        var described = PropertyKeysDescribedInTypeScript();

        described.Should().BeEquivalentTo(Enum.GetNames<ConnectorPropertyKey>());
    }

    private static IReadOnlyList<string> PropertyKeysDescribedInTypeScript()
    {
        var path = Path.Combine(RepositoryTree.Root(), "src", "Web", "packages", "app", "src", "lib",
            "config", "connectorPropertyMeta.ts");
        var lines = File.ReadAllLines(path);

        var start = Array.FindIndex(lines,
            line => line.StartsWith("export const connectorPropertyMeta", StringComparison.Ordinal));
        if (start < 0)
            throw new InvalidOperationException($"No connectorPropertyMeta declaration in {path}.");

        var end = Array.FindIndex(lines, start + 1,
            line => line.StartsWith("}", StringComparison.Ordinal));
        if (end < 0)
            throw new InvalidOperationException($"connectorPropertyMeta is never closed in {path}.");

        return [.. lines[(start + 1)..end]
            .Select(line => MetaEntry.Match(line))
            .Where(match => match.Success)
            .Select(match => match.Groups[1].Value)];
    }
}

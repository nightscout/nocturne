using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Nocturne.Connectors.Core.Tests.Services;

/// <summary>
/// No connector may build its own HTTP transport. A <c>new HttpClient(...)</c> is invisible to
/// <c>IHttpClientFactory</c>, so it carries no guard, no pinned connect, and — because
/// <c>ConnectorClientGuardCoverageTests</c> can only inspect clients the factory knows about — no
/// coverage either. CareLink and Tandem each had one for a year.
/// </summary>
/// <remarks>
/// A source scan rather than a reflection one: the offending code is a constructor call inside a
/// method, which no amount of type inspection reveals, and the point is to fail on the next one
/// being written rather than on it being reached at runtime.
/// <para>
/// Two files may construct a transport. <c>HttpClientExtensions</c> is the factory registration
/// every connector client goes through, and <c>OutboundHttpClient</c> is the one escape hatch, for
/// the vendor login flows that need a cookie jar per attempt — both build a pinned transport, which
/// is the property this test exists to keep universal.
/// </para>
/// </remarks>
public partial class ConnectorHttpClientConstructionTests
{
    private static readonly string[] MayBuildATransport =
    [
        "HttpClientExtensions.cs",
        "OutboundHttpClient.cs",
    ];

    [GeneratedRegex(@"new\s+(HttpClient|HttpClientHandler|SocketsHttpHandler)\s*[({]")]
    private static partial Regex TransportConstruction();

    [Fact]
    public void NoConnectorConstructsItsOwnHttpTransport()
    {
        var offenders = ConnectorSources()
            .Where(file => !MayBuildATransport.Contains(Path.GetFileName(file)))
            .Where(file => TransportConstruction().IsMatch(File.ReadAllText(file)))
            .Select(file => Path.GetFileName(file))
            .ToList();

        offenders.Should().BeEmpty(
            "a connector's HTTP client must come from IHttpClientFactory via " +
            "ConfigureConnectorClient, or from OutboundHttpClient when it needs its own cookie " +
            "jar; anything else opts out of the address guard and out of the coverage test");
    }

    [Fact]
    public void TheScanSeesTheConnectorSources()
    {
        // Without this the scan above passes by finding no files at all — a moved directory or a
        // changed build layout would turn it into a test of nothing.
        var sources = ConnectorSources();

        sources.Should().HaveCountGreaterThan(100,
            "the connector projects are hundreds of files; finding almost none means the scan " +
            "lost the source tree rather than that the tree is clean");

        foreach (var allowed in MayBuildATransport)
        {
            sources.Select(Path.GetFileName).Should().Contain(allowed,
                "the allowlist has to name files that exist, or it is quietly permitting nothing");
        }
    }

    private static List<string> ConnectorSources()
    {
        var connectors = Path.Combine(RepositoryTree.Root(), "src", "Connectors");

        return [.. Directory.EnumerateFiles(connectors, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                           && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))];
    }
}

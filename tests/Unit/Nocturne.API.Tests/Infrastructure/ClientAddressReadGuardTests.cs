using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// The connection's address is the SvelteKit container's for a request it made for a browser, so a
/// recorder that reads it names the wrong client. Everything in the API takes the client's address
/// from <c>HttpContext.GetClientAddress()</c>, which <c>ClientAddressResolver</c> settles.
/// </summary>
[Trait("Category", "Unit")]
public class ClientAddressReadGuardTests
{
    /// <summary>The two members that read the connection: the resolver and the fallback beside the accessor.</summary>
    private static readonly string[] Readers =
    [
        "src/API/Nocturne.API/Authorization/ClientAddressResolver.cs: ClientAddressResolver.Resolve",
        "src/API/Nocturne.API/Extensions/HttpContextExtensions.cs: HttpContextExtensions.GetClientAddress",
    ];

    [Fact]
    public void Nothing_in_the_API_reads_the_connections_address_but_the_resolver()
    {
        Reads().Except(Readers).Should().BeEmpty(
            "a recorder reading Connection.RemoteIpAddress records the web container, not the client");
    }

    [Fact]
    public void The_listed_readers_still_read_it()
    {
        Readers.Except(Reads()).Should().BeEmpty("a stale entry would let the next reader in that member through");
    }

    private static IEnumerable<string> Reads() =>
        RepoSources.Under("src/API")
            .SelectMany(file => ConnectionAddressReads(CSharpSyntaxTree.ParseText(file.Text))
                .Select(node => $"{file.Path}: {EnclosingMember.Of(node)}"))
            .Distinct();

    [Theory]
    [InlineData("class C { string? M(HttpContext c) => c.Connection.RemoteIpAddress?.ToString(); }", true)]
    [InlineData("class C { object? M(HttpContext c) => c?.Connection?.RemoteIpAddress; }", true)]
    [InlineData("class C { object? M(ConnectionInfo c) { var a = c.RemoteIpAddress; return a; } }", true)]
    [InlineData("class C { string? M(HttpContext c) => c.GetClientAddress(); }", false)]
    [InlineData("class C { string M => \"from {RemoteIpAddress}\"; }", false)]
    public void DetectsAReadOfTheConnectionsAddress(string source, bool expected) =>
        ConnectionAddressReads(CSharpSyntaxTree.ParseText(source)).Any().Should().Be(expected);

    private static IEnumerable<SyntaxNode> ConnectionAddressReads(SyntaxTree tree)
    {
        var nodes = tree.GetRoot().DescendantNodes();
        return nodes.OfType<MemberAccessExpressionSyntax>()
            .Where(access => access.Name.Identifier.ValueText == "RemoteIpAddress")
            .Cast<SyntaxNode>()
            .Concat(nodes.OfType<MemberBindingExpressionSyntax>()
                .Where(binding => binding.Name.Identifier.ValueText == "RemoteIpAddress"));
    }
}

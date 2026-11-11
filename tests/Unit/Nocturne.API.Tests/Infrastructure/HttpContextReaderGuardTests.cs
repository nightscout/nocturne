using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Nocturne.API.Hubs;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// The live <see cref="HttpContext"/> is safe to read only on the thread of the request or the hub
/// call it belongs to. Every type that takes an <see cref="IHttpContextAccessor"/> and every member
/// that reaches a connection's upgrade request (<c>GetHttpContext</c>, or the
/// <see cref="IHttpContextFeature"/> behind it) is pinned here, grouped by why it runs there, so a new
/// reader is a decision rather than an accident.
/// </summary>
/// <remarks>
/// The match is syntactic: a mention by name, qualified or not, including a generic argument, a
/// method group, a <c>using static</c> call and a <c>using</c> alias of the type. It does not follow a
/// reflection call that names none of these. <see cref="HubConnectionRevalidator"/> is held to more: a
/// call to any pinned member counts as a read there, since the member reads the upgrade request for it.
/// </remarks>
[Trait("Category", "Unit")]
public class HttpContextReaderGuardTests
{
    private static readonly string[] AccessorTypes = [nameof(IHttpContextAccessor), nameof(HttpContextAccessor)];

    private static readonly string[] ConnectionRequestRoutes =
        [nameof(GetHttpContextExtensions.GetHttpContext), nameof(IHttpContextFeature)];

    private static readonly string[] AccessorConsumers =
    [
        // The request path: scoped or transient services that read the request they are resolved in.
        "src/API/Nocturne.API/Authorization/HasPermissionsRequirement.cs: HasPermissionsHandler",
        "src/API/Nocturne.API/Services/Auth/AuthAuditService.cs: AuthAuditService",
        "src/API/Nocturne.API/Services/CoachMarks/CoachMarkService.cs: CoachMarkService",
        "src/API/Nocturne.API/Services/NotificationActionHandlers/AlertActionHandler.cs: AlertActionHandler",
        "src/API/Nocturne.API/Services/Platform/AlexaService.cs: AlexaService",
        "src/API/Nocturne.API/Services/Platform/StatusService.cs: StatusService",
        "src/API/Nocturne.API/Services/TranslationDraftService.cs: TranslationDraftService",
    ];

    private static readonly string[] ConnectionRequestReaders =
    [
        // Admission: the connect that admits the connection, with its upgrade request in hand.
        "src/API/Nocturne.API/Hubs/TenantAwareHub.cs: TenantAwareHub.OnConnectedAsync",
        "src/API/Nocturne.API/Hubs/TenantHubFilter.cs: TenantHubFilter.OnConnectedAsync",

        // Hub invocation: a hub method, or a helper of the connection's own calls (connect, invocation,
        // disconnect). SignalR runs those one at a time while the transport holds the upgrade request
        // or its long-polling copy. Work off them reads what the connection holds instead
        // (HubConnectionReader.Held, HubAuthorizationState.Granted).
        "src/API/Nocturne.API/Hubs/AlertHub.cs: AlertHub.Acknowledge",
        "src/API/Nocturne.API/Hubs/DataHub.cs: DataHub.Authorize",
        "src/API/Nocturne.API/Hubs/DataHub.cs: DataHub.LoadRetro",
        "src/API/Nocturne.API/Hubs/HomeAssistantHub.cs: HomeAssistantHub.Acknowledge",
        "src/API/Nocturne.API/Hubs/HomeAssistantHub.cs: HomeAssistantHub.CatchUpFailedDeliveriesAsync",
        "src/API/Nocturne.API/Hubs/HubAuthorization.cs: HubAuthorizationState.Grant",
        "src/API/Nocturne.API/Hubs/HubAuthorization.cs: HubAuthorizationState.Resolve",
        "src/API/Nocturne.API/Hubs/HubConnectionActor.cs: HubConnectionActor.Of",
        "src/API/Nocturne.API/Hubs/HubConnectionActor.cs: HubConnectionActor.Record",
        "src/API/Nocturne.API/Hubs/HubConnectionReader.cs: HubConnectionReader.Of",
        "src/API/Nocturne.API/Hubs/OverviewHub.cs: OverviewHub.Authorize",
        "src/API/Nocturne.API/Hubs/TenantAwareHub.cs: TenantAwareHub.TenantContext",
    ];

    private const string RevalidatorPath = "src/API/Nocturne.API/Hubs/HubConnectionRevalidator.cs";

    private static readonly IReadOnlyList<SourceFile> Candidates =
        RepoSources.Src.Where(s => MayMention(s.Text)).ToList();

    private static readonly IReadOnlyList<string> Consumers = Scan(AccessorTypes, EnclosingMember.TypeOf);

    private static readonly IReadOnlyList<string> Readers = Scan(ConnectionRequestRoutes, EnclosingMember.Of);

    private static readonly ILookup<string, string> PinnedMembers = ConnectionRequestReaders
        .Select(entry => entry[(entry.IndexOf(": ", StringComparison.Ordinal) + 2)..].Split('.'))
        .ToLookup(parts => parts[^2], parts => parts[^1]);

    [Fact]
    public void OnlyThePinnedTypesTakeAnHttpContextAccessor()
    {
        Consumers.Except(AccessorConsumers).Should().BeEmpty(
            "a type taking IHttpContextAccessor reads whichever request is current on its thread; "
            + "pin it here under the reason it is safe, or pass it what it needs");
    }

    [Fact]
    public void EveryPinnedAccessorConsumerStillTakesOne()
    {
        AccessorConsumers.Except(Consumers).Should().BeEmpty("a stale entry would silently cover a future reader");
    }

    [Fact]
    public void OnlyThePinnedMembersReadAConnectionsUpgradeRequest()
    {
        Readers.Except(ConnectionRequestReaders).Should().BeEmpty(
            "work off the connection's own calls reads what the connection holds; "
            + "pin a member here under the reason it is safe");
    }

    [Fact]
    public void EveryPinnedReaderStillReadsOne()
    {
        ConnectionRequestReaders.Except(Readers).Should().BeEmpty("a stale entry would silently cover a future reader");
    }

    [Theory]
    [InlineData("class C { C(IHttpContextAccessor a) { } }", "C")]
    [InlineData("class C(IHttpContextAccessor a) { }", "C")]
    [InlineData("class C { private readonly IHttpContextAccessor _a; }", "C")]
    [InlineData("class C { IHttpContextAccessor A { get; } }", "C")]
    [InlineData("class C { C(Microsoft.AspNetCore.Http.IHttpContextAccessor a) { } }", "C")]
    [InlineData("class O { class C { C(IHttpContextAccessor? a) { } } }", "O.C")]
    [InlineData("class C { void M(IServiceProvider p) { var a = p.GetRequiredService<IHttpContextAccessor>(); } }", "C")]
    [InlineData("class C { void M() { var a = new HttpContextAccessor(); } }", "C")]
    [InlineData("using Acc = Microsoft.AspNetCore.Http.IHttpContextAccessor; class C(Acc a) { }", "C")]
    [InlineData("using Acc = Microsoft.AspNetCore.Http.IHttpContextAccessor; class C { void M(Acc a) { } }", "C")]
    [InlineData("using Acc = Microsoft.AspNetCore.Http.HttpContextAccessor; class C { void M() { new Acc(); } }", "C")]
    [InlineData("using Acc = Microsoft.AspNetCore.Http.HttpContext; class C { void M(Acc a) { } }", null)]
    [InlineData("class C { C(IHttp\\u0043ontextAccessor a) { } }", "C")]
    [InlineData("class C { void M(IServiceCollection s) { s.AddHttpContextAccessor(); } }", null)]
    [InlineData("class C { void M(HttpContext c) { var r = c.Request; } }", null)]
    [InlineData("class C { void M() { } } // IHttpContextAccessor", null)]
    public void FindsTheAccessorConsumer(string source, string? expected)
    {
        AssertFinds(source, AccessorTypes, EnclosingMember.TypeOf, expected);
    }

    [Theory]
    [InlineData("class C { void M(HubCallerContext c) { var h = c.GetHttpContext(); } }", "C.M")]
    [InlineData("class C { void M(HubCallerContext? c) { var h = c?.GetHttpContext(); } }", "C.M")]
    [InlineData("class C { object P => Context.GetHttpContext()!.RequestServices; }", "C.P")]
    [InlineData("class C { void M(HubCallerContext c) { var h = GetHttpContextExtensions.GetHttpContext(c); } }", "C.M")]
    [InlineData("using static Microsoft.AspNetCore.SignalR.GetHttpContextExtensions; class C { void M(HubCallerContext c) { var h = GetHttpContext(c); } }", "C.M")]
    [InlineData("class C { void M(IEnumerable<HubCallerContext> c) { var h = c.Select(GetHttpContextExtensions.GetHttpContext); } }", "C.M")]
    [InlineData("class C { void M(HubCallerContext c) { var h = c.Features.Get<IHttpContextFeature>(); } }", "C.M")]
    [InlineData("class C { void M(HubCallerContext c) { var h = c.Get\\u0048ttpContext(); } }", "C.M")]
    [InlineData("using F = Microsoft.AspNetCore.Http.Connections.Features.IHttpContextFeature; class C { void M(HubCallerContext c) { var h = c.Features.Get<F>(); } }", "C.M")]
    [InlineData("class C { void M(HubCallerContext c) { var i = c.Items; } }", null)]
    [InlineData("class C { void M(ControllerBase c) { var h = c.HttpContext; } }", null)]
    public void FindsTheUpgradeRequestReader(string source, string? expected)
    {
        AssertFinds(source, ConnectionRequestRoutes, EnclosingMember.Of, expected);
    }

    [Fact]
    public void TheRevalidatorReadsNoUpgradeRequest()
    {
        var revalidator = RepoSources.Src.Single(s => s.Path == RevalidatorPath);

        var reads = Readers.Where(r => r.StartsWith(RevalidatorPath + ":", StringComparison.Ordinal))
            .Concat(PinnedCalls(revalidator.Tree));

        reads.Should().BeEmpty(
            "the sweep runs off the connection's own calls, while the transport may be using the request; "
            + "read what the connection holds (HubConnectionReader.Held, HubAuthorizationState.Granted)");
    }

    [Theory]
    [InlineData("class C { void M(HubCallerContext c) { HubAuthorizationState.Resolve(c); } }", "HubAuthorizationState.Resolve")]
    [InlineData("class C { void M(HubCallerContext c) { HubConnectionReader.Of(c); } }", "HubConnectionReader.Of")]
    [InlineData("class C { void M(HubCallerContext c) { var t = HubConnectionReader.Of(c)?.Tenant; } }", "HubConnectionReader.Of")]
    [InlineData("class C { void M(HubCallerContext c) { Nocturne.API.Hubs.HubConnectionActor.Record(c, null); } }", "HubConnectionActor.Record")]
    [InlineData("class C { void M(HubCallerContext c) { global::Nocturne.API.Hubs.HubAuthorizationState.Grant(c, null); } }", "HubAuthorizationState.Grant")]
    [InlineData("class C { void M(IEnumerable<HubCallerContext> c) { c.Select(HubAuthorizationState.Resolve); } }", "HubAuthorizationState.Resolve")]
    [InlineData("using R = Nocturne.API.Hubs.HubConnectionReader; class C { void M(HubCallerContext c) { R.Of(c); } }", "R.Of")]
    [InlineData("using static Nocturne.API.Hubs.HubConnectionReader; class C { void M(HubCallerContext c) { Of(c); } }", "HubConnectionReader (using static)")]
    [InlineData("class C { void M(HubCallerContext c) { HubConnectionReader.Held(c); HubAuthorizationState.Granted(c); } }", null)]
    [InlineData("class C { void M(Other c) { c.Resolve(); Other.Of(c); } }", null)]
    public void FindsACallToAPinnedReader(string source, string? expected)
    {
        var found = PinnedCalls(CSharpSyntaxTree.ParseText(source));

        found.Should().Equal(expected is null ? [] : [expected]);
    }

    /// <summary>
    /// The calls in <paramref name="tree"/> to a member pinned under <see cref="ConnectionRequestReaders"/>,
    /// written <c>Type.Member</c>, and the <c>using static</c> of a pinned type. A pinned member reads
    /// the upgrade request on its caller's behalf, so the caller reads it too.
    /// </summary>
    private static List<string> PinnedCalls(SyntaxTree tree)
    {
        var root = tree.GetRoot();
        var aliases = AliasesOf([tree], PinnedMembers.Select(types => types.Key));
        string Resolve(string name) => aliases.GetValueOrDefault(name, name);

        var calls = root.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .Where(access => RightmostName(access.Expression) is { } type
                && PinnedMembers[Resolve(type)].Contains(access.Name.Identifier.ValueText))
            .Select(access => $"{RightmostName(access.Expression)}.{access.Name.Identifier.ValueText}");

        var imports = root.DescendantNodes().OfType<UsingDirectiveSyntax>()
            .Where(u => u.StaticKeyword.IsKind(SyntaxKind.StaticKeyword)
                && RightmostName(u.NamespaceOrType) is { } type
                && PinnedMembers.Contains(Resolve(type)))
            .Select(u => $"{RightmostName(u.NamespaceOrType)} (using static)");

        return calls.Concat(imports).Distinct().ToList();
    }

    private static void AssertFinds(
        string source, string[] names, Func<SyntaxNode, string> describe, string? expected)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var found = Mentions(tree, WithAliases([tree], names), describe);

        if (expected is null)
        {
            found.Should().BeEmpty();
            return;
        }

        found.Should().Equal(expected);
        MayMention(source).Should().BeTrue("the scan does not parse a file this filter turns away");
    }

    private static List<string> Scan(string[] names, Func<SyntaxNode, string> describe)
    {
        var all = WithAliases(Candidates.Select(s => s.Tree), names);
        var aliases = all.Except(names).ToList();

        var files = aliases.Count == 0
            ? Candidates
            : RepoSources.Src.Where(s => MayMention(s.Text) || aliases.Any(a => s.Text.Contains(a, StringComparison.Ordinal)));

        return files
            .SelectMany(s => Mentions(s.Tree, all, describe).Select(member => $"{s.Path}: {member}"))
            .ToList();
    }

    /// <summary>
    /// <paramref name="names"/> and every alias a <c>using</c> in <paramref name="trees"/> gives one of
    /// them. A <c>global using</c> alias reaches files that never mention the type, so the scan reads
    /// the aliases of every candidate before it chooses the files.
    /// </summary>
    private static HashSet<string> WithAliases(IEnumerable<SyntaxTree> trees, IEnumerable<string> names)
    {
        var known = names.ToHashSet(StringComparer.Ordinal);
        known.UnionWith(AliasesOf(trees, known).Keys);
        return known;
    }

    /// <summary>Each alias a <c>using</c> in <paramref name="trees"/> gives one of <paramref name="names"/>, with the name it stands for.</summary>
    private static Dictionary<string, string> AliasesOf(IEnumerable<SyntaxTree> trees, IEnumerable<string> names)
    {
        var known = names.ToHashSet(StringComparer.Ordinal);

        return trees
            .SelectMany(tree => tree.GetRoot().DescendantNodes().OfType<UsingDirectiveSyntax>())
            .Where(u => u.Alias is not null
                && !u.StaticKeyword.IsKind(SyntaxKind.StaticKeyword)
                && RightmostName(u.NamespaceOrType) is { } target
                && known.Contains(target))
            .GroupBy(u => u.Alias!.Name.Identifier.ValueText)
            .ToDictionary(g => g.Key, g => RightmostName(g.First().NamespaceOrType)!);
    }

    private static string? RightmostName(SyntaxNode node) => node switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
        AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        _ => null,
    };

    private static List<string> Mentions(SyntaxTree tree, HashSet<string> names, Func<SyntaxNode, string> describe) =>
        tree.GetRoot().DescendantNodes()
            .OfType<SimpleNameSyntax>()
            .Where(n => names.Contains(n.Identifier.ValueText) && !n.Ancestors().OfType<UsingDirectiveSyntax>().Any())
            .Select(n => describe(n))
            .Distinct()
            .ToList();

    /// <summary>
    /// Whether <paramref name="text"/> can mention a name matched here. Every one of them holds
    /// <c>HttpContext</c>; a file with a <c>\u</c> escape is parsed whatever it holds, since
    /// identifiers are matched by value.
    /// </summary>
    private static bool MayMention(string text) =>
        text.Contains(nameof(HttpContext), StringComparison.Ordinal)
        || text.Contains(@"\u", StringComparison.OrdinalIgnoreCase);
}

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Nocturne.API.Services.NotificationTemplates;
using Nocturne.API.Services.Notifications;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// Deleting an account removes the notices <see cref="NotificationSubjectNaming"/> declares as naming
/// the person. A notice type outside the registry, or a template that does not say whether it names a
/// person, would leave a name in someone else's inbox with nothing to catch it. Every type created
/// under <c>src</c> has a template, and every template's <see cref="NotificationTemplate.NamesSubject"/>
/// agrees with the catalogue.
/// </summary>
/// <remarks>
/// The scan reads syntax only. It follows the type argument through a literal, a constant, a method
/// returning constants and the parameter of a private method to its callers in the same file. A type it cannot read is
/// reported, unless the member is listed as taking its type from the caller or as passing it through
/// a path the scan cannot follow, in which case the listed constants are checked instead and a new
/// type passed that way is not seen.
/// </remarks>
[Trait("Category", "Unit")]
public class NotificationTypeGuardTests
{
    private static readonly HashSet<string> CreateMethods =
        ["CreateNotificationAsync", "CreateNotificationUnlessActiveAsync"];

    private static readonly HashSet<string> CallerSupplied =
    [
        "src/API/Nocturne.API/Controllers/V4/Monitoring/NotificationsController.cs: NotificationsController.CreateNotification",
    ];

    private static readonly Dictionary<string, string[]> PassedThroughATuple = new()
    {
        ["src/API/Nocturne.API/Services/Platform/Releases/ReleaseNoticeJob.cs: ReleaseNoticeJob.NotifyOwnerAsync"] =
            ["ReleaseNoticeJob.UpdateAvailableType", "ReleaseNoticeJob.ReleaseInstalledType"],
    };

    private static readonly IReadOnlyList<NotificationTemplate> Templates =
        [.. new NotificationTemplateRegistry().AddBuiltInTemplates().Templates];

    [Fact]
    public void EveryCreatedNoticeTypeCanBeRead()
    {
        Unreadable(RepoSources.Src, CallerSupplied, PassedThroughATuple).Should().BeEmpty(
            "the guard cannot tell whether a type it cannot read has a template");
    }

    [Fact]
    public void EveryCreatedNoticeTypeHasATemplate()
    {
        Unregistered(RepoSources.Src, Templates.Select(t => t.Type), PassedThroughATuple).Should().BeEmpty(
            "register the type in BuiltInNotificationTemplates and say whether it names a person");
    }

    [Fact]
    public void EveryListedMemberStillHasAnUnreadableType()
    {
        Stale(RepoSources.Src, CallerSupplied.Concat(PassedThroughATuple.Keys)).Should().BeEmpty(
            "a stale entry would silently cover a future creation in that member");
    }

    [Fact]
    public void EveryTemplateNamingAPersonIsDeclaredAndEveryDeclaredTypeHasOne()
    {
        Templates.Where(t => t.NamesSubject).Select(t => t.Type).Should().BeEquivalentTo(
            NotificationSubjectNaming.Pointers.Keys,
            "the template's statement and the catalogue the deletion reads are one fact");
    }

    [Fact]
    public void RegisteringATypeTwiceIsRefused()
    {
        var registry = new NotificationTemplateRegistry();
        var template = new NotificationTemplate { Type = "x.type", Category = NotificationCategory.Informational, NamesSubject = false };
        registry.Register(template);

        var again = () => registry.Register(
            new NotificationTemplate { Type = "x.type", Category = NotificationCategory.Informational, NamesSubject = true });

        again.Should().Throw<InvalidOperationException>();
        registry.GetTemplate("x.type").Should().BeSameAs(template);
    }

    [Fact]
    public void AnUnregisteredLiteralConstantAndMethodResultAreReported()
    {
        var sources = Sources(
            "class A { const string Kept = \"kept.type\"; const string Lost = \"lost.type\"; "
            + "void M(IInAppNotificationService n) { "
            + "n.CreateNotificationAsync(\"u\", \"literal.type\", \"t\"); "
            + "n.CreateNotificationAsync(userId: \"u\", type: Kept, title: \"t\"); "
            + "n.CreateNotificationUnlessActiveAsync(\"u\", Lost, \"t\", \"s\"); "
            + "n.CreateNotificationAsync(\"u\", Pick(1), \"t\"); } "
            + "static string Pick(int i) => i > 0 ? \"first.type\" : Kept; }");

        Unregistered(sources, ["kept.type"], new()).Should().Equal(
            "literal.type", "lost.type", "first.type");
    }

    [Fact]
    public void APrivateMethodsParameterIsFollowedToItsCallersInTheFile()
    {
        var sources = Sources(
            "class A { const string Seen = \"seen.type\"; "
            + "void M(IInAppNotificationService n) { Tell(n, Seen); this.Tell(n, \"other.type\"); } "
            + "private void Tell(IInAppNotificationService n, string type) { n.CreateNotificationAsync(\"u\", type, \"t\"); } }");

        Unregistered(sources, ["seen.type"], new()).Should().Equal("other.type");
    }

    [Fact]
    public void ANonPrivateMethodsParameterIsReportedUnreadableForItsCallersMayBeElsewhere()
    {
        var sources = Sources(
            "class A { void M(IInAppNotificationService n) { Tell(n, \"seen.type\"); } "
            + "public void Tell(IInAppNotificationService n, string type) { n.CreateNotificationAsync(\"u\", type, \"t\"); } }");

        Unreadable(sources, [], new()).Should().Equal($"{FilePath}: A.Tell");
    }

    [Fact]
    public void ATypeTheScanCannotReadIsReportedUnlessListed()
    {
        var sources = Sources(
            "class A { void M(IInAppNotificationService n, Req r) { n.CreateNotificationAsync(\"u\", r.Type, \"t\"); } }");

        Unreadable(sources, [], new()).Should().Equal($"{FilePath}: A.M");
        Unreadable(sources, [$"{FilePath}: A.M"], new()).Should().BeEmpty();
    }

    [Fact]
    public void AListedMemberHasItsListedConstantsChecked()
    {
        var sources = Sources(
            "class A { const string Seen = \"seen.type\"; const string Missed = \"missed.type\"; "
            + "void M(IInAppNotificationService n, Req r) { n.CreateNotificationAsync(\"u\", r.Type, \"t\"); } }");
        var listed = new Dictionary<string, string[]> { [$"{FilePath}: A.M"] = ["A.Seen", "A.Missed"] };

        Unregistered(sources, ["seen.type"], listed).Should().Equal("missed.type");
    }

    [Fact]
    public void AListedMemberWithNothingUnreadableIsStale()
    {
        var sources = Sources(
            "class A { void M(IInAppNotificationService n) { n.CreateNotificationAsync(\"u\", \"x.type\", \"t\"); } }");

        Stale(sources, [$"{FilePath}: A.M", $"{FilePath}: A.Gone"]).Should().Equal($"{FilePath}: A.M", $"{FilePath}: A.Gone");
    }

    [Fact]
    public void AHelperOfTheSameNameThatIsNotCalledOnAServiceIsNotACreation()
    {
        var sources = Sources(
            "class A { void M() { CreateNotificationAsync(1, 2); } "
            + "void CreateNotificationAsync(int night, int count) { } }");

        Unregistered(sources, [], new()).Should().BeEmpty();
        Unreadable(sources, [], new()).Should().BeEmpty();
    }

    private const string FilePath = "src/API/A.cs";

    private static IReadOnlyList<SourceFile> Sources(string code) => [new SourceFile(FilePath, code)];

    private static List<string> Unreadable(
        IReadOnlyList<SourceFile> sources, IEnumerable<string> callerSupplied, Dictionary<string, string[]> passedThrough) =>
        Creations(sources)
            .Where(c => c.Types.Contains(null)
                        && !callerSupplied.Contains(c.Site)
                        && !passedThrough.ContainsKey(c.Site))
            .Select(c => c.Site)
            .Distinct()
            .ToList();

    private static List<string> Unregistered(
        IReadOnlyList<SourceFile> sources, IEnumerable<string> registered, Dictionary<string, string[]> passedThrough)
    {
        var reader = new TypeReader(sources);
        return Creations(sources)
            .SelectMany(c => c.Types.Where(t => t is not null).Select(t => t!)
                .Concat(passedThrough.GetValueOrDefault(c.Site, []).Select(reader.Constant)))
            .Except(registered)
            .Distinct()
            .ToList();
    }

    private static List<string> Stale(IReadOnlyList<SourceFile> sources, IEnumerable<string> listed)
    {
        var unreadable = Creations(sources).Where(c => c.Types.Contains(null)).Select(c => c.Site).ToHashSet();
        return listed.Where(site => !unreadable.Contains(site)).ToList();
    }

    private sealed record Creation(string Site, IReadOnlyList<string?> Types);

    private static List<Creation> Creations(IReadOnlyList<SourceFile> sources)
    {
        var reader = new TypeReader(sources);
        return sources
            .SelectMany(file => file.Tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(IsNoticeCreation)
                .Select(call => new Creation(
                    $"{file.Path}: {EnclosingMember.Of(call)}",
                    TypeArgument(call) is { } type ? [.. reader.Read(type)] : [null])))
            .ToList();
    }

    private static bool IsNoticeCreation(InvocationExpressionSyntax call) => call.Expression switch
    {
        MemberAccessExpressionSyntax member => CreateMethods.Contains(member.Name.Identifier.ValueText),
        MemberBindingExpressionSyntax binding => CreateMethods.Contains(binding.Name.Identifier.ValueText),
        _ => false,
    };

    private static ExpressionSyntax? TypeArgument(InvocationExpressionSyntax call)
    {
        var arguments = call.ArgumentList.Arguments;
        return arguments.FirstOrDefault(a => a.NameColon?.Name.Identifier.ValueText == "type")?.Expression
               ?? (arguments.ElementAtOrDefault(1) is { NameColon: null } positional ? positional.Expression : null);
    }

    private sealed class TypeReader
    {
        private const int MaxDepth = 6;

        private readonly Dictionary<(string Owner, string Name), string> _constants;

        public TypeReader(IReadOnlyList<SourceFile> sources) =>
            _constants = sources
                .SelectMany(file => file.Tree.GetRoot().DescendantNodes().OfType<FieldDeclarationSyntax>()
                    .Where(field => field.Modifiers.Any(SyntaxKind.ConstKeyword))
                    .SelectMany(field => field.Declaration.Variables
                        .Where(v => v.Initializer?.Value is LiteralExpressionSyntax literal
                                    && literal.IsKind(SyntaxKind.StringLiteralExpression))
                        .Select(v => (
                            Key: (Owner: OwnerOf(field), Name: v.Identifier.ValueText),
                            Value: ((LiteralExpressionSyntax)v.Initializer!.Value).Token.ValueText))))
                .GroupBy(c => c.Key)
                .ToDictionary(g => g.Key, g => g.First().Value);

        public string Constant(string reference)
        {
            var parts = reference.Split('.');
            return _constants.TryGetValue((parts[0], parts[1]), out var value)
                ? value
                : throw new InvalidOperationException($"{reference} is not a string constant under src.");
        }

        public IEnumerable<string?> Read(ExpressionSyntax expression, int depth = 0)
        {
            if (depth > MaxDepth)
                return [null];

            switch (expression)
            {
                case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression):
                    return [literal.Token.ValueText];
                case ParenthesizedExpressionSyntax parenthesized:
                    return Read(parenthesized.Expression, depth);
                case ConditionalExpressionSyntax conditional:
                    return Read(conditional.WhenTrue, depth).Concat(Read(conditional.WhenFalse, depth));
                case SwitchExpressionSyntax switched:
                    return switched.Arms.SelectMany(arm => Read(arm.Expression, depth));
                case IdentifierNameSyntax name:
                    return ReadName(name, depth);
                case MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax owner } member:
                    return [_constants.GetValueOrDefault((owner.Identifier.ValueText, member.Name.Identifier.ValueText))];
                case InvocationExpressionSyntax { Expression: IdentifierNameSyntax method }:
                    return ReadReturns(method, depth);
                default:
                    return [null];
            }
        }

        private IEnumerable<string?> ReadName(IdentifierNameSyntax name, int depth)
        {
            var id = name.Identifier.ValueText;
            if (_constants.TryGetValue((OwnerOf(name), id), out var constant))
                return [constant];

            var method = name.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
            var index = method?.ParameterList.Parameters.ToList().FindIndex(p => p.Identifier.ValueText == id) ?? -1;
            if (method is null || index < 0 || !method.Modifiers.Any(SyntaxKind.PrivateKeyword))
                return [null];

            var callers = name.SyntaxTree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(c => CalledName(c) == method.Identifier.ValueText)
                .ToList();

            return callers.Count == 0
                ? [null]
                : callers.SelectMany(c => ArgumentFor(c, id, index) is { } argument ? Read(argument, depth + 1) : [null]);
        }

        private static string? CalledName(InvocationExpressionSyntax call) => call.Expression switch
        {
            IdentifierNameSyntax name => name.Identifier.ValueText,
            MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } member => member.Name.Identifier.ValueText,
            _ => null,
        };

        private IEnumerable<string?> ReadReturns(IdentifierNameSyntax method, int depth)
        {
            var declarations = method.SyntaxTree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Where(m => m.Identifier.ValueText == method.Identifier.ValueText)
                .ToList();
            if (declarations.Count == 0)
                return [null];

            return declarations.SelectMany(m => m.ExpressionBody is { } body
                ? Read(body.Expression, depth + 1)
                : (m.Body?.DescendantNodes().OfType<ReturnStatementSyntax>() ?? [])
                    .SelectMany(r => r.Expression is { } returned ? Read(returned, depth + 1) : [null]));
        }

        private static ExpressionSyntax? ArgumentFor(InvocationExpressionSyntax call, string parameter, int index)
        {
            var arguments = call.ArgumentList.Arguments;
            return arguments.FirstOrDefault(a => a.NameColon?.Name.Identifier.ValueText == parameter)?.Expression
                   ?? (arguments.ElementAtOrDefault(index) is { NameColon: null } positional ? positional.Expression : null);
        }

        private static string OwnerOf(SyntaxNode node) =>
            node.Ancestors().OfType<BaseTypeDeclarationSyntax>().First().Identifier.ValueText;
    }
}

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// The members of a source that name a method or construct a type, each written
/// <c>path: Type.Member</c> (<see cref="EnclosingMember"/>), for the guards that pin who may do a thing.
/// </summary>
internal static class SourceMentions
{
    /// <summary>
    /// The members around every mention of one of <paramref name="methods"/> by name: a call, a
    /// conditional call (<c>f?.Method(...)</c>) or a method group passed on. The receiver's type is not
    /// read, so every method of that name counts.
    /// </summary>
    public static IEnumerable<string> Calling(SourceFile source, IReadOnlyCollection<string> methods) =>
        methods.Any(method => source.Text.Contains(method, StringComparison.Ordinal))
            ? Where(source, node => node switch
            {
                MemberAccessExpressionSyntax member => methods.Contains(member.Name.Identifier.ValueText),
                MemberBindingExpressionSyntax binding => methods.Contains(binding.Name.Identifier.ValueText),
                IdentifierNameSyntax name => methods.Contains(name.Identifier.ValueText)
                    && name.Parent is not (MemberAccessExpressionSyntax or MemberBindingExpressionSyntax),
                _ => false,
            })
            : [];

    /// <summary>
    /// The members that construct one of <paramref name="types"/> with <c>new</c>. A type listed with
    /// its qualifier (<c>SignInAdmission.Account</c>) matches only when the source writes the qualifier
    /// too, so a nested type of the same name in another type is not counted. A target-typed
    /// <c>new(...)</c> writes no type and is not seen.
    /// </summary>
    public static IEnumerable<string> Constructing(SourceFile source, IReadOnlyCollection<string> types) =>
        types.Any(type => source.Text.Contains(type[(type.LastIndexOf('.') + 1)..], StringComparison.Ordinal))
            ? Where(source, node => node is ObjectCreationExpressionSyntax creation
                && types.Any(type => Names(creation.Type.ToString(), type)))
            : [];

    /// <summary>
    /// The members that name one of <paramref name="types"/>: as the type of a field, property,
    /// parameter or local, a type argument or in <c>nameof</c>. A type that implements one, naming it
    /// in its base list, is not counted.
    /// </summary>
    public static IEnumerable<string> Naming(SourceFile source, IReadOnlyCollection<string> types) =>
        types.Any(type => source.Text.Contains(type, StringComparison.Ordinal))
            ? Where(source, node => node is IdentifierNameSyntax name
                && types.Contains(name.Identifier.ValueText)
                && name.FirstAncestorOrSelf<BaseListSyntax>() is null)
            : [];

    /// <summary>
    /// The members that read <c><paramref name="type"/>.<paramref name="member"/></c>, an enum value
    /// or a constant, however the type is qualified. A <c>using static</c> import of the type, which
    /// leaves it unwritten, is not seen.
    /// </summary>
    public static IEnumerable<string> Reading(SourceFile source, string type, string member) =>
        source.Text.Contains(type, StringComparison.Ordinal)
            ? Where(source, node => node is MemberAccessExpressionSyntax access
                && access.Name.Identifier.ValueText == member
                && Rightmost(access.Expression) == type)
            : [];

    /// <summary>The last name an expression is written with: <c>Name</c> in <c>a.b.Name</c>.</summary>
    public static string Rightmost(ExpressionSyntax expression) => expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        SimpleNameSyntax name => name.Identifier.ValueText,
        PostfixUnaryExpressionSyntax suppressed => Rightmost(suppressed.Operand),
        ParenthesizedExpressionSyntax parenthesized => Rightmost(parenthesized.Expression),
        _ => expression.ToString(),
    };

    /// <summary>The members holding a node <paramref name="matches"/> accepts.</summary>
    public static IEnumerable<string> Where(SourceFile source, Func<SyntaxNode, bool> matches) =>
        source.Tree.GetRoot()
            .DescendantNodes()
            .Where(matches)
            .Select(EnclosingMember.Of)
            .Distinct()
            .Select(member => $"{source.Path}: {member}");

    private static bool Names(string written, string type)
    {
        var compact = string.Concat(written.Where(c => !char.IsWhiteSpace(c)));
        return compact == type || compact.EndsWith("." + type, StringComparison.Ordinal);
    }
}

/// <summary>
/// What a guard pins: the members allowed to do one thing, each with the reason it may, and how a
/// source is searched for the members that do it.
/// </summary>
/// <param name="Rule">What the members do, said when an unlisted one is found.</param>
internal sealed record AllowlistFence(
    string Rule,
    IReadOnlyDictionary<string, string> Allowed,
    Func<SourceFile, IEnumerable<string>> Find)
{
    public IReadOnlyList<string> FoundIn(IEnumerable<SourceFile> sources) =>
        sources.SelectMany(Find).Distinct().ToList();

    /// <summary>Members that do it and are not allowed to.</summary>
    public IReadOnlyList<string> Unlisted(IEnumerable<SourceFile> sources) =>
        FoundIn(sources).Except(Allowed.Keys).ToList();

    /// <summary>Entries that no longer match a member, which would silently cover a future one.</summary>
    public IReadOnlyList<string> Stale(IEnumerable<SourceFile> sources) =>
        Allowed.Keys.Except(FoundIn(sources)).ToList();
}

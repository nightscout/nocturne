using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// Names the member a syntax node sits in, qualified by its enclosing types (<c>Outer.Inner.Method</c>),
/// so a source guard's allowlist can name a member rather than a whole file. A local function or
/// lambda counts as the member it is written in.
/// </summary>
internal static class EnclosingMember
{
    public static string Of(SyntaxNode node)
    {
        var member = node.Ancestors().OfType<MemberDeclarationSyntax>()
            .FirstOrDefault(m => m is not BaseTypeDeclarationSyntax);

        var name = member switch
        {
            null => "(type)",
            MethodDeclarationSyntax method => method.Identifier.ValueText,
            ConstructorDeclarationSyntax constructor => constructor.Identifier.ValueText,
            BasePropertyDeclarationSyntax property => property switch
            {
                PropertyDeclarationSyntax p => p.Identifier.ValueText,
                EventDeclarationSyntax e => e.Identifier.ValueText,
                _ => "this[]",
            },
            BaseFieldDeclarationSyntax field => string.Join(",", field.Declaration.Variables.Select(v => v.Identifier.ValueText)),
            OperatorDeclarationSyntax op => $"operator {op.OperatorToken.ValueText}",
            _ => member.Kind().ToString(),
        };

        var types = (member ?? node).Ancestors().OfType<BaseTypeDeclarationSyntax>()
            .Reverse()
            .Select(type => type.Identifier.ValueText);

        return string.Join(".", types.Append(name));
    }
}

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// The strings a source builds, for the guards that read the SQL in them: each literal, each
/// interpolated string and each concatenation as one statement, with anything built at runtime as
/// <c>{}</c>.
/// </summary>
internal static class SqlLiterals
{
    public static IEnumerable<(SyntaxNode Node, string Text)> In(SyntaxTree tree)
    {
        foreach (var node in tree.GetRoot().DescendantNodes())
        {
            switch (node)
            {
                case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression):
                    yield return (node, literal.Token.ValueText);
                    break;
                case InterpolatedStringExpressionSyntax interpolated:
                    yield return (node, Render(interpolated));
                    break;
                case BinaryExpressionSyntax concatenation
                    when concatenation.IsKind(SyntaxKind.AddExpression)
                        && concatenation.Parent?.IsKind(SyntaxKind.AddExpression) != true
                        && Operands(concatenation).Any(IsString):
                    yield return (node, string.Concat(Operands(concatenation).Select(Render)));
                    break;
            }
        }
    }

    private static IEnumerable<ExpressionSyntax> Operands(BinaryExpressionSyntax concatenation) =>
        new[] { concatenation.Left, concatenation.Right }.SelectMany(e =>
            e is BinaryExpressionSyntax inner && inner.IsKind(SyntaxKind.AddExpression) ? Operands(inner) : [e]);

    private static bool IsString(ExpressionSyntax e) =>
        e.IsKind(SyntaxKind.StringLiteralExpression) || e is InterpolatedStringExpressionSyntax;

    private static string Render(ExpressionSyntax e) => e switch
    {
        LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression) => literal.Token.ValueText,
        InterpolatedStringExpressionSyntax interpolated => string.Concat(interpolated.Contents.Select(c =>
            c is InterpolatedStringTextSyntax text ? text.TextToken.ValueText : "{}")),
        _ => "{}",
    };
}

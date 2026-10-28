using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nocturne.Tests.Shared.Infrastructure;

/// <summary>A read of a static clock, or a reach for <c>TimeProvider.System</c>.</summary>
/// <param name="File">Repository-relative path with forward slashes; empty for a parsed string.</param>
/// <param name="Line">One-based line.</param>
/// <param name="Read">The expression or using directive as written.</param>
public sealed record StaticClockRead(string File, int Line, string Read)
{
    public override string ToString() => $"{File}:{Line}: {Read}";
}

/// <summary>
/// Finds static clock reads by syntax: <c>DateTime.Now/UtcNow/Today</c> and
/// <c>DateTimeOffset.Now/UtcNow</c>, however qualified or aliased, a <c>using static</c> of either
/// type, and <c>TimeProvider.System</c>. The clock guards of each project scan their migrated folders
/// with it.
/// </summary>
public static class StaticClockReads
{
    private static readonly HashSet<string> ClockTypes = [nameof(DateTime), nameof(DateTimeOffset)];

    private static readonly HashSet<string> StaticClockMembers =
        [nameof(DateTime.UtcNow), nameof(DateTime.Now), nameof(DateTime.Today)];

    /// <summary>Every read in the <c>.cs</c> files under each repository-relative folder.</summary>
    /// <exception cref="DirectoryNotFoundException">A folder does not exist.</exception>
    public static List<StaticClockRead> InFolders(IEnumerable<string> folders)
    {
        var root = RepositoryRoot();
        var reads = new List<StaticClockRead>();

        foreach (var folder in folders)
        {
            var directory = Path.Combine(root, folder);
            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException($"{folder} is listed as a migrated folder but does not exist.");

            foreach (var path in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                reads.AddRange(In(File.ReadAllText(path)).Select(r => r with { File = relative }));
            }
        }

        return reads;
    }

    /// <summary>Every read in <paramref name="source"/>.</summary>
    public static List<StaticClockRead> In(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        var reads = new List<StaticClockRead>();

        void Report(SyntaxNode node) =>
            reads.Add(new StaticClockRead("", tree.GetLineSpan(node.Span).StartLinePosition.Line + 1, node.ToString()));

        var clockTypes = new HashSet<string>(ClockTypes);
        foreach (var directive in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
        {
            var target = LastName(directive.NamespaceOrType);
            if (!ClockTypes.Contains(target))
                continue;

            if (directive.StaticKeyword.IsKind(SyntaxKind.StaticKeyword))
                Report(directive);
            else if (directive.Alias is { } alias)
                clockTypes.Add(alias.Name.Identifier.ValueText);
        }

        foreach (var access in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            var type = LastName(access.Expression);
            var member = access.Name.Identifier.ValueText;
            if ((clockTypes.Contains(type) && StaticClockMembers.Contains(member))
                || (type == nameof(TimeProvider) && member == nameof(TimeProvider.System)))
                Report(access);
        }

        return reads;
    }

    private static string LastName(SyntaxNode node) => node switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
        AliasQualifiedNameSyntax aliased => aliased.Name.Identifier.ValueText,
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        _ => string.Empty,
    };

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "tests", "Unit")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"No tests/Unit directory above {AppContext.BaseDirectory}.");
    }
}

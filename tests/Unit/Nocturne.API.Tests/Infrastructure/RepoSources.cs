using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// The repository's C# sources, read once per test process for the guards that scan them. Build
/// output is left out, as are the generated EF migration designers and model snapshot: they hold
/// only model-builder calls and are most of the lines under <c>src</c>. A guard with a rule for
/// the model's column defaults (<c>HistoryClampLiftPremiseTests</c>) reads the snapshot on its own.
/// </summary>
internal static class RepoSources
{
    public static string Root { get; } = FindRoot();

    private static readonly Lazy<IReadOnlyList<SourceFile>> SrcFiles =
        new(() => Read("src"), LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<IReadOnlyList<SourceFile>> TestFiles =
        new(() => Read("tests"), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Every source under <c>src</c>, in path order.</summary>
    public static IReadOnlyList<SourceFile> Src => SrcFiles.Value;

    /// <summary>Every source under <c>tests</c>, in path order.</summary>
    public static IReadOnlyList<SourceFile> Tests => TestFiles.Value;

    /// <summary>The sources under a repository-relative directory such as <c>src/API</c>.</summary>
    /// <exception cref="DirectoryNotFoundException">The directory is not in the checkout, so a
    /// guard listing a renamed one fails rather than scanning nothing.</exception>
    public static IEnumerable<SourceFile> Under(string directory)
    {
        if (!Directory.Exists(Path.Combine(Root, directory)))
            throw new DirectoryNotFoundException($"{directory} is not under {Root}.");

        var prefix = directory.TrimEnd('/') + "/";
        var tree = prefix.StartsWith("src/", StringComparison.Ordinal) ? Src
            : prefix.StartsWith("tests/", StringComparison.Ordinal) ? Tests
            : throw new ArgumentException($"{directory} is under neither src nor tests.", nameof(directory));

        return tree.Where(file => file.Path.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static List<SourceFile> Read(string tree) =>
        Directory.EnumerateFiles(Path.Combine(Root, tree), "*.cs", SearchOption.AllDirectories)
            .Select(path => (Absolute: path, Relative: Path.GetRelativePath(Root, path).Replace('\\', '/')))
            .Where(file => !IsBuildOutput(file.Relative) && !IsGeneratedMigration(file.Relative))
            .AsParallel()
            .Select(file => new SourceFile(file.Relative, File.ReadAllText(file.Absolute)))
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToList();

    private static bool IsBuildOutput(string relativePath) =>
        relativePath.Contains("/obj/", StringComparison.Ordinal)
        || relativePath.Contains("/bin/", StringComparison.Ordinal);

    private static bool IsGeneratedMigration(string path) =>
        path.EndsWith(".Designer.cs", StringComparison.Ordinal)
        || path.EndsWith("ModelSnapshot.cs", StringComparison.Ordinal);

    private static string FindRoot()
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

/// <summary>One source file: its repository-relative path with forward slashes, its text, and its syntax tree, parsed on first use.</summary>
internal sealed class SourceFile(string path, string text)
{
    private readonly Lazy<SyntaxTree> _tree =
        new(() => CSharpSyntaxTree.ParseText(text), LazyThreadSafetyMode.ExecutionAndPublication);

    public string Path { get; } = path;

    public string Text { get; } = text;

    public SyntaxTree Tree => _tree.Value;
}

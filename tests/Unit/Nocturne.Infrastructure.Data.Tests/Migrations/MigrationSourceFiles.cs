using System.ComponentModel.DataAnnotations.Schema;
using System.Reflection;
using System.Text.RegularExpressions;
using Match = System.Text.RegularExpressions.Match;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Reads shipped migrations as source text. A migration's SQL is a string handed to PostgreSQL, so
/// nothing about it is reachable through the EF model or through a SQLite test database; the file
/// is the only thing a test can hold it to.
/// </summary>
internal static class MigrationSourceFiles
{
    /// <summary>Every migration source file, excluding Designer files and the model snapshot.</summary>
    public static IReadOnlyList<string> All() =>
        Directory.GetFiles(Location(), "*.cs")
            .Where(f => !f.EndsWith(".Designer.cs", StringComparison.Ordinal))
            .Where(f => !Path.GetFileName(f).Contains("ModelSnapshot", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Source text of the single migration whose name ends with <paramref name="name"/>, which is
    /// the part of the file name after the timestamp.
    /// </summary>
    public static string Text(string name) =>
        File.ReadAllText(All().Single(f =>
            Path.GetFileNameWithoutExtension(f).EndsWith("_" + name, StringComparison.Ordinal)));

    /// <summary>Migration name — the file name a guard reports and an allowlist keys on.</summary>
    public static string Name(string file) => Path.GetFileNameWithoutExtension(file);

    /// <summary>
    /// Text of the migration's <c>Up</c> method. Only <c>Up</c> runs on the startup migration
    /// chain, and <c>Down</c> routinely recreates the very indexes <c>Up</c> replaced. Sliced at
    /// the <c>Down</c> signature rather than by brace matching: migration SQL lives in
    /// interpolated raw string literals whose <c>{table}</c> holes are not C# braces. A shape
    /// this fails to recognise yields an empty body, which reports as a discovery regression
    /// rather than as a pass.
    /// </summary>
    public static string UpBody(string source)
    {
        var start = source.IndexOf("void Up(", StringComparison.Ordinal);

        if (start < 0)
            return string.Empty;

        var end = source.IndexOf("void Down(", start, StringComparison.Ordinal);

        return end < 0 ? source[start..] : source[start..end];
    }

    /// <summary>
    /// The same text with every C# and SQL comment overwritten by spaces, so a commented-out
    /// statement cannot satisfy a guard while offsets stay comparable to the original. A comment
    /// marker inside a string literal is blanked too, which can only withhold evidence from a
    /// guard, never manufacture it.
    /// </summary>
    public static string WithCommentsBlanked(string source)
    {
        var blanked = source.ToCharArray();

        foreach (Match match in Comment.Matches(source))
            for (var i = match.Index; i < match.Index + match.Length; i++)
                if (blanked[i] is not ('\r' or '\n'))
                    blanked[i] = ' ';

        return new string(blanked);
    }

    /// <summary>Table names of every <see cref="ITenantScoped"/> entity, lowercased.</summary>
    public static IReadOnlySet<string> TenantScopedTableNames() =>
        typeof(ITenantScoped).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ITenantScoped).IsAssignableFrom(t))
            .Select(t => t.GetCustomAttribute<TableAttribute>()?.Name)
            .Where(n => n is not null)
            .Select(n => n!.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

    private static readonly Regex Comment = new(
        @"/\*.*?\*/|//[^\r\n]*|--[^\r\n]*",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static string Location()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName, "src", "Infrastructure", "Nocturne.Infrastructure.Data", "Migrations");

            if (Directory.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"No migrations directory above {AppContext.BaseDirectory}.");
    }
}

using System.Text.RegularExpressions;
using Nocturne.Infrastructure.Data.Security;
using Match = System.Text.RegularExpressions.Match;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Reads shipped migrations as source text. A migration's SQL is a string handed to PostgreSQL, so
/// nothing about it is reachable through the EF model or through a SQLite test database; the file
/// is the only thing a test can hold it to.
/// </summary>
internal static class MigrationSourceFiles
{
    /// <summary>
    /// Every migration source file. A migration is timestamp-prefixed, so anything else sharing
    /// the folder — the model snapshot, a shared SQL-constants helper — is not one, and is skipped
    /// rather than handed to a parser that would reject it.
    /// </summary>
    public static IReadOnlyList<string> All() =>
        Directory.GetFiles(Location(), "*.cs")
            .Where(f => !f.EndsWith(".Designer.cs", StringComparison.Ordinal))
            .Where(f => TimestampPrefixed.IsMatch(Path.GetFileNameWithoutExtension(f)))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Source text of the single migration whose name ends with <paramref name="name"/>, which is
    /// the part of the file name after the timestamp.
    /// </summary>
    public static string Text(string name) =>
        File.ReadAllText(All().Single(f =>
            Path.GetFileNameWithoutExtension(f).EndsWith("_" + name, StringComparison.Ordinal)));

    /// <summary>
    /// The bare table name in a captured SQL or builder reference: the leading segment, before any
    /// argument list, alias or trailing punctuation, unquoted and lowercased. <c>tbl(col)</c>,
    /// <c>"tbl"(col)</c> and <c>tbl (col)</c> all yield <c>tbl</c>. A derived table
    /// (<c>(VALUES …)</c>) yields an empty string, which matches no table.
    /// </summary>
    public static string BareTableName(string captured) =>
        Regex.Split(captured, @"[(;,\s]")[0].Trim('"').ToLowerInvariant();

    /// <summary>Migration name — the file name a guard reports and an allowlist keys on.</summary>
    public static string Name(string file) => Path.GetFileNameWithoutExtension(file);

    /// <summary>
    /// Text of the migration's <c>Up</c> method. Only <c>Up</c> runs on the startup migration
    /// chain, and <c>Down</c> routinely recreates the very indexes <c>Up</c> replaced. Sliced at
    /// the <c>Down</c> signature rather than by brace matching: migration SQL lives in
    /// interpolated raw string literals whose <c>{table}</c> holes are not C# braces.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The signature was not found. Returning an empty body instead would silently clear every
    /// guard that scans it, so a shape this cannot parse has to stop the run.
    /// </exception>
    public static string UpBody(string file)
    {
        var source = File.ReadAllText(file);
        var start = source.IndexOf("void Up(", StringComparison.Ordinal);

        if (start < 0)
            throw new InvalidOperationException(
                $"No 'void Up(' in {Name(file)}; a guard cannot scan a method it cannot find.");

        var end = source.IndexOf("void Down(", start, StringComparison.Ordinal);

        return end < 0 ? source[start..] : source[start..end];
    }

    /// <summary>
    /// The same text with every C# and SQL comment overwritten by spaces, so a commented-out
    /// statement cannot satisfy a guard while offsets stay comparable to the original.
    /// <para>
    /// A comment marker inside a string literal is blanked too, so this withholds evidence rather
    /// than manufacturing it. That direction is only safe on a path where missing evidence means
    /// FAIL — recognising a cleanup, or recognising the new-table exemption. On a path that
    /// detects offenders, withheld evidence is a false green, so scan raw text there instead.
    /// </para>
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

    /// <summary>
    /// <see cref="ShareRlsPolicy.TenantScopedTableNames"/> as a lowercased set, for comparison
    /// against table names lifted out of SQL by <see cref="BareTableName"/>.
    /// </summary>
    public static IReadOnlySet<string> TenantScopedTableNames() => ScopedTables.Value;

    private static readonly Lazy<IReadOnlySet<string>> ScopedTables = new(() =>
    {
        using var context = OfflineDbContext.Create();

        return ShareRlsPolicy.TenantScopedTableNames(context.Model)
            .Select(n => n.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
    });

    private static readonly Regex TimestampPrefixed = new(@"^\d{14}_", RegexOptions.Compiled);

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

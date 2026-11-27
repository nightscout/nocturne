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
    /// argument list, alias or trailing punctuation, unqualified, unquoted and lowercased.
    /// <c>tbl(col)</c>, <c>tbl)</c>, <c>public.tbl</c> and <c>"tbl"</c> all yield <c>tbl</c>. The
    /// capture is migration source, not SQL, so the whitespace ending the name may be a C# escape
    /// sequence and a quote around it is escaped as well: the escaped quote is unescaped first,
    /// then a backslash ends the name like any other delimiter. A derived table
    /// (<c>(VALUES …)</c>) yields an empty string, which matches no table.
    /// </summary>
    public static string BareTableName(string captured) =>
        Regex.Split(Unescaped(captured), @"[();,\s\\]")[0]
            .Split('.')[^1]
            .Trim('"')
            .ToLowerInvariant();

    /// <summary>
    /// A captured reference with C#-escaped quotes turned back into quotes, for anything that has
    /// to recognise the shape of a reference rather than just its leading segment.
    /// </summary>
    public static string Unescaped(string captured) =>
        captured.Replace("\\\"", "\"", StringComparison.Ordinal);

    /// <summary>
    /// What <see cref="ResolveTable"/> yields for a target that is not a plain name, which no
    /// <c>CreateTable</c> can match, so a guard reports it instead of skipping it.
    /// </summary>
    public const string UnresolvedTable = "<table not resolvable to a literal name>";

    /// <summary>
    /// The table a captured reference names, or <see cref="UnresolvedTable"/> when it is not a
    /// plain name: an interpolated <c>{table}</c> hole, or a loop variable.
    /// </summary>
    public static string ResolveTable(string captured)
    {
        var bare = BareTableName(captured);

        return PlainTableName.IsMatch(bare) ? bare : UnresolvedTable;
    }

    /// <summary>
    /// The table a builder call targets: the named <c>table:</c> argument, or for a positional
    /// call the second string literal. <c>CreateIndex</c>, <c>AddUniqueConstraint</c> and
    /// <c>AddPrimaryKey</c> all take (name, table, …).
    /// </summary>
    public static string FluentTable(string arguments) =>
        TableArgument.Match(arguments) is { Success: true } named
            ? ResolveTable(named.Groups[1].Value)
            : PositionalTableArgument.Match(arguments) is { Success: true } positional
                ? ResolveTable(positional.Groups[1].Value)
                : UnresolvedTable;

    /// <summary>
    /// The tables a migration's <c>Up</c> creates, which hold no rows yet. A table that is not a
    /// literal name is left out, so it exempts nothing.
    /// </summary>
    public static IReadOnlySet<string> TablesCreatedIn(string up) =>
        new[] { FluentCreateTable, SqlCreateTable }
            .SelectMany(r => r.Matches(up).Select(m => ResolveTable(m.Groups[1].Value)))
            .Where(t => t != UnresolvedTable)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// A unique or primary key constraint added to <see cref="Table"/> at <see cref="Position"/>.
    /// <see cref="UsesExistingIndex"/> is the raw <c>USING INDEX</c> attach of an index the migration
    /// built earlier, which builds nothing; every other form builds its own index.
    /// </summary>
    public readonly record struct KeyConstraint(int Position, string Table, bool UsesExistingIndex);

    /// <summary>
    /// The fluent <c>AddUniqueConstraint</c> and <c>AddPrimaryKey</c> calls in <paramref name="up"/>, and
    /// every clause of a raw <c>ALTER TABLE</c> that adds a unique or primary key constraint:
    /// <c>ADD [CONSTRAINT name] UNIQUE | PRIMARY KEY</c>, or a column added with either, which builds its
    /// index just the same. Each has the table it names, which is <see cref="UnresolvedTable"/> when
    /// not a literal.
    /// </summary>
    public static IEnumerable<KeyConstraint> KeyConstraintsIn(string up)
    {
        foreach (Match match in FluentKeyConstraint.Matches(up))
            yield return new(match.Index, FluentTable(match.Groups[1].Value), false);

        foreach (Match statement in AlterTable.Matches(up))
        {
            var table = ResolveTable(statement.Groups[1].Value);

            foreach (var clause in TopLevelClauses(statement.Groups[2].Value))
            {
                if (ConstraintClause.Match(clause) is { Success: true } constraint)
                    yield return new(statement.Index, table, constraint.Groups[1].Success);
                else if (ColumnClause.IsMatch(clause) && ColumnKey.IsMatch(clause))
                    yield return new(statement.Index, table, false);
            }
        }
    }

    /// <summary>
    /// <paramref name="actions"/> split at the commas outside parentheses, so a column type such as
    /// <c>numeric(10, 2)</c> stays in one clause.
    /// </summary>
    private static IEnumerable<string> TopLevelClauses(string actions)
    {
        var depth = 0;
        var start = 0;

        for (var i = 0; i < actions.Length; i++)
        {
            switch (actions[i])
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
                case ',' when depth <= 0:
                    yield return actions[start..i];
                    start = i + 1;
                    break;
            }
        }

        yield return actions[start..];
    }

    /// <summary>Migration name — the file name a guard reports and an allowlist keys on.</summary>
    public static string Name(string file) => Path.GetFileNameWithoutExtension(file);

    /// <summary>
    /// Whole source text, for the questions <see cref="UpBody"/> cannot answer: a migration's
    /// table list is routinely a class-level array its <c>Up</c> only loops over.
    /// </summary>
    public static string Source(string file) => File.ReadAllText(file);

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
        var source = Source(file);
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
    public static string WithCommentsBlanked(string source) => Blanked(source, Comment);

    /// <summary>
    /// <see cref="WithCommentsBlanked"/>, additionally blanking single-quoted SQL literals, for
    /// questions about SQL structure that a keyword inside a literal would answer wrongly.
    /// Comments go first, so an apostrophe in prose cannot pair with a real literal's quote.
    /// </summary>
    public static string WithCommentsAndSqlLiteralsBlanked(string source) =>
        Blanked(WithCommentsBlanked(source), SqlLiteral);

    private static string Blanked(string source, Regex region)
    {
        var blanked = source.ToCharArray();

        foreach (Match match in region.Matches(source))
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

    private static readonly Regex PlainTableName = new(@"^\w+$", RegexOptions.Compiled);

    private static readonly Regex FluentCreateTable = new(
        @"\.CreateTable\s*\(\s*name\s*:\s*""([^""]+)""", RegexOptions.Compiled);

    private static readonly Regex TableArgument = new(@"table\s*:\s*""([^""]+)""", RegexOptions.Compiled);

    private static readonly Regex PositionalTableArgument = new(
        @"^\s*""[^""]*""\s*,\s*""([^""]+)""", RegexOptions.Compiled);

    private static readonly Regex SqlCreateTable = new(
        @"CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?(\S+)", Sql);

    private static readonly Regex FluentKeyConstraint = new(
        @"\.(?:AddUniqueConstraint|AddPrimaryKey)\s*\(([^;]*?)\)\s*;", RegexOptions.Compiled);

    private const RegexOptions Sql = RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled;

    /// <summary>The table and the rest of the statement, up to its semicolon.</summary>
    private static readonly Regex AlterTable = new(
        @"ALTER\s+TABLE\s+(?:IF\s+EXISTS\s+)?(?:ONLY\s+)?(\S+)([^;]*)", Sql);

    /// <summary>
    /// A constraint clause. <c>ADD</c> is searched for rather than anchored, so the quotes, pluses and
    /// names of a statement split across literals cannot hide it. The name is anything up to the key
    /// words, so a quoted name with a space and a name built by concatenation read the same way; group 1
    /// is a <c>USING INDEX</c> attach.
    /// </summary>
    private static readonly Regex ConstraintClause = new(
        @"\bADD\s+(?:CONSTRAINT\b.*?)?\b(?:UNIQUE|PRIMARY\s+KEY)\b(\s+USING\s+INDEX\b)?", Sql);

    private static readonly Regex ColumnClause = new(@"\bADD\s+(?!CONSTRAINT\b)", Sql);

    private static readonly Regex ColumnKey = new(@"\b(?:UNIQUE|PRIMARY\s+KEY)\b", Sql);

    private static readonly Regex TimestampPrefixed = new(@"^\d{14}_", RegexOptions.Compiled);

    private static readonly Regex SqlLiteral = new(
        "'[^']*'",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex Comment = new(
        @"/\*.*?\*/|//[^\r\n]*|--[^\r\n]*",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static string Location()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName, "src", "Infrastructure", "Nocturne.Infrastructure.Data.Migrations", "Migrations");

            if (Directory.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"No migrations directory above {AppContext.BaseDirectory}.");
    }
}

using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Nocturne.Infrastructure.Data.Interceptors;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// The context pool skips Npgsql's session reset on pool return, so whatever a lessee leaves on the
/// session reaches the next one; <see cref="TenantConnectionInterceptor"/> overwrites only the RLS
/// GUCs and <c>application_name</c>. No SQL in app code may leave anything else: a session advisory
/// lock, a <c>LISTEN</c>, a temporary table, a holdable cursor or a session <c>SET</c>. Transaction-scoped
/// forms (<c>pg_advisory_xact_lock</c>, <c>SET LOCAL</c>, <c>set_config(..., true)</c>) end with the
/// transaction. Migrations run on the migrator's own pool, which keeps the reset.
/// </summary>
[Trait("Category", "Unit")]
public partial class SessionStateGuardTests
{
    /// <summary>
    /// Members that write a session-level <c>set_config</c>, each writing only GUCs the interceptor
    /// overwrites on the next open.
    /// </summary>
    private static readonly string[] SessionGucWriters =
    [
        "src/Infrastructure/Nocturne.Infrastructure.Data/Extensions/RlsPinningExtensions.cs: RlsPinningExtensions.PinTenantAsync",
        "src/Infrastructure/Nocturne.Infrastructure.Data/Interceptors/TenantConnectionInterceptor.cs: TenantConnectionInterceptor.SessionSql",
    ];

    private static readonly IReadOnlyList<(string Path, SyntaxTree Tree)> Sources = RepoSources.Src
        .Where(s => !s.Path.StartsWith("src/Infrastructure/Nocturne.Infrastructure.Data.Migrations/", StringComparison.Ordinal))
        .Select(s => (s.Path, s.Tree))
        .ToList();

    [Fact]
    public void NoAppSqlLeavesSessionState()
    {
        Sources
            .SelectMany(s => SqlLiterals(s.Tree)
                .Where(l => LeavesSessionState(l.Text))
                .Select(l => $"{s.Path}: {l.Text.Trim()}"))
            .Should().BeEmpty("a pooled connection keeps it for the next lessee; use the transaction-scoped form");
    }

    [Fact]
    public void OnlyTheInterceptorsGucsAreWrittenForTheSession()
    {
        var writers = Sources
            .SelectMany(s => SqlLiterals(s.Tree)
                .Where(l => WritesASessionGuc(l.Text))
                .Select(l => $"{s.Path}: {EnclosingMember.Of(l.Node)}"))
            .Distinct()
            .ToList();

        writers.Except(SessionGucWriters).Should().BeEmpty(
            "a session GUC the interceptor does not overwrite reaches the next lessee; use set_config(..., true)");
        SessionGucWriters.Except(writers).Should().BeEmpty("a stale entry would cover a future writer in that member");
    }

    /// <summary>
    /// A context's connection opened by hand skips the interceptor and so reads the previous lessee's
    /// session; open it with <c>Database.OpenConnection</c> or <c>OpenConnectionAsync</c>.
    /// </summary>
    [Fact]
    public void NoContextConnectionIsOpenedByHand()
    {
        Sources
            .SelectMany(s => RawContextOpens(s.Tree).Select(n => $"{s.Path}: {EnclosingMember.Of(n)}"))
            .Should().BeEmpty("only EF's open runs TenantConnectionInterceptor");
    }

    [Theory]
    [InlineData("class C { async Task M(DbContext db) => await db.Database.GetDbConnection().OpenAsync(); }", true)]
    [InlineData("class C { void M(DbContext db) { var c = db.Database.GetDbConnection(); c.Open(); } }", true)]
    [InlineData("class C { async Task M(DbContext db) { var c = db.Database.GetDbConnection(); if (c.State != Open) await c.OpenAsync(); } }", true)]
    [InlineData("class C { async Task M(DbContext db) { await db.Database.OpenConnectionAsync(); var c = db.Database.GetDbConnection(); } }", false)]
    [InlineData("class C { async Task M(NpgsqlDataSource ds) { await using var c = ds.CreateConnection(); await c.OpenAsync(); } }", false)]
    public void DetectsAContextConnectionOpenedByHand(string source, bool expected) =>
        RawContextOpens(CSharpSyntaxTree.ParseText(source)).Any().Should().Be(expected);

    [Theory]
    [InlineData("SELECT pg_advisory_lock(1)", true)]
    [InlineData("SELECT pg_try_advisory_lock(1, 2)", true)]
    [InlineData("SELECT pg_advisory_lock_shared(1)", true)]
    [InlineData("SELECT pg_advisory_xact_lock(1)", false)]
    [InlineData("SELECT pg_try_advisory_xact_lock(1)", false)]
    [InlineData("LISTEN alerts", true)]
    [InlineData("SELECT 1; listen alerts", true)]
    [InlineData("CREATE TEMP TABLE t (id int)", true)]
    [InlineData("create temporary table t (id int)", true)]
    [InlineData("SELECT * INTO TEMP t FROM x", true)]
    [InlineData("DECLARE c CURSOR WITH HOLD FOR SELECT 1", true)]
    [InlineData("SET statement_timeout = 0", true)]
    [InlineData("SET ROLE nocturne_migrator", true)]
    [InlineData("SET SESSION AUTHORIZATION x", true)]
    [InlineData("SELECT 1;\n SET search_path TO x", true)]
    [InlineData("SET LOCAL lock_timeout = '3s'", false)]
    [InlineData("UPDATE t SET a = 1", false)]
    [InlineData("UPDATE t\nSET a = 1", false)]
    [InlineData("Listening on port 80", false)]
    [InlineData("Set another profile active first, then delete this one.", false)]
    [InlineData("set statement_timeout to 0", true)]
    [InlineData("Set statement_timeout = 0", true)]
    [InlineData("SET {} = {}", true)]
    [InlineData("SET {} TO '{}'", true)]
    [InlineData("SET \"search_path\" = x", true)]
    [InlineData("SET SESSION CHARACTERISTICS AS TRANSACTION ISOLATION LEVEL SERIALIZABLE", true)]
    [InlineData("SET SESSION {} = {}", true)]
    [InlineData("Set Local lock_timeout = '3s'", false)]
    [InlineData("SET LOCAL {} = {}", false)]
    [InlineData("SET TRANSACTION ISOLATION LEVEL SERIALIZABLE", false)]
    [InlineData("Set PatientId to one of: {}", false)]
    public void DetectsSessionState(string sql, bool expected) =>
        LeavesSessionState(sql).Should().Be(expected);

    [Theory]
    [InlineData("SELECT set_config('app.x', @v, false)", true)]
    [InlineData("SELECT set_config({0}, {1}, false)", true)]
    [InlineData("SELECT set_config({}, {}, {})", true)]
    [InlineData("SELECT set_config({0}, {1}, {2})", true)]
    [InlineData("SELECT set_config('app.x', @v, @local)", true)]
    [InlineData("SELECT set_config('app.x', 'v', 'false')", true)]
    [InlineData("SELECT SET_CONFIG('app.x', 'v', FALSE)", true)]
    [InlineData("SELECT set_config('app.x', {}", true)]
    [InlineData("SELECT set_config('app.x', {0}, true)", false)]
    [InlineData("SELECT set_config('app.x', coalesce(@v, 'a,b'), TRUE)", false)]
    [InlineData("SELECT set_config('app.a', 'x', true), set_config('app.b', @v, false)", true)]
    public void DetectsASessionSetConfig(string sql, bool expected) =>
        WritesASessionGuc(sql).Should().Be(expected);

    [Fact]
    public void ReadsInterpolatedAndRawLiterals()
    {
        var tree = CSharpSyntaxTree.ParseText(""""
            class C
            {
                string A(int x) => $"SELECT pg_advisory_lock({x})";
                string B => """
                    LISTEN alerts
                    """;
            }
            """");

        SqlLiterals(tree).Select(l => LeavesSessionState(l.Text)).Should().Equal(true, true);
    }

    [Fact]
    public void ReadsAConcatenationAsOneStatement()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class C
            {
                string A(string name, string value) => "SET " + name + " = " + value;
            }
            """);

        SqlLiterals(tree).Select(l => l.Text).Should().Contain("SET {} = {}");
    }

    private static IEnumerable<SyntaxNode> RawContextOpens(SyntaxTree tree)
    {
        var root = tree.GetRoot();
        var contextConnections = root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Where(v => v.Initializer?.Value.ToString().Contains("GetDbConnection()", StringComparison.Ordinal) == true)
            .Select(v => v.Identifier.ValueText)
            .ToHashSet(StringComparer.Ordinal);

        return root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression is MemberAccessExpressionSyntax
            {
                Name.Identifier.ValueText: "Open" or "OpenAsync",
                Expression: var receiver,
            } && (receiver.ToString().EndsWith("GetDbConnection()", StringComparison.Ordinal)
                || receiver is IdentifierNameSyntax { Identifier.ValueText: var name } && contextConnections.Contains(name)));
    }

    private static bool LeavesSessionState(string sql) =>
        SessionAdvisoryLock().IsMatch(sql)
        || Listen().IsMatch(sql)
        || TempTable().IsMatch(sql)
        || HoldableCursor().IsMatch(sql)
        || SessionSet().IsMatch(sql);

    private static IEnumerable<(SyntaxNode Node, string Text)> SqlLiterals(SyntaxTree tree)
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

    /// <summary>A string as its text, with anything built at runtime as <c>{}</c>.</summary>
    private static string Render(ExpressionSyntax e) => e switch
    {
        LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression) => literal.Token.ValueText,
        InterpolatedStringExpressionSyntax interpolated => string.Concat(interpolated.Contents.Select(c =>
            c is InterpolatedStringTextSyntax text ? text.TextToken.ValueText : "{}")),
        _ => "{}",
    };

    /// <summary>
    /// A <c>set_config</c> whose third argument is anything but the literal <c>true</c>, including one
    /// built at runtime or cut off by it, writes for the session.
    /// </summary>
    private static bool WritesASessionGuc(string sql) =>
        SetConfigCall().Matches(sql).Any(m => ThirdArgument(sql, m.Index + m.Length) is not { } local
            || !local.Equals("true", StringComparison.OrdinalIgnoreCase));

    private static string? ThirdArgument(string sql, int start)
    {
        var depth = 0;
        var quoted = false;
        var commas = 0;
        var argumentStart = start;
        for (var i = start; i < sql.Length; i++)
        {
            var c = sql[i];
            if (c == '\'')
                quoted = !quoted;
            else if (quoted)
                continue;
            else if (c == '(')
                depth++;
            else if (c == ',' && depth == 0)
            {
                commas++;
                argumentStart = i + 1;
            }
            else if (c == ')' && depth-- == 0)
                return commas == 2 ? sql[argumentStart..i].Trim() : null;
        }

        return null;
    }

    [GeneratedRegex(@"\bpg_(try_)?advisory_lock(_shared)?\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex SessionAdvisoryLock();

    [GeneratedRegex(@"(^|;)\s*LISTEN\s+\w+\s*(;|$)", RegexOptions.IgnoreCase)]
    private static partial Regex Listen();

    [GeneratedRegex(@"\b(CREATE\s+(GLOBAL\s+|LOCAL\s+)?|INTO\s+)TEMP(ORARY)?\s+(TABLE\s+)?\w", RegexOptions.IgnoreCase)]
    private static partial Regex TempTable();

    [GeneratedRegex(@"\bWITH\s+HOLD\b", RegexOptions.IgnoreCase)]
    private static partial Regex HoldableCursor();

    /// <summary>
    /// A setting's name is matched in lower case, as Postgres folds an unquoted one, so that log text
    /// such as <c>Set PatientId to ...</c> is not read as SQL.
    /// </summary>
    [GeneratedRegex(
        @"(^|;)\s*SET\s+(SESSION\s+)?(ROLE\b|SESSION\s+AUTHORIZATION\b|CHARACTERISTICS\b|TIME\s+ZONE\b|(?!LOCAL\b)(\{\}|""[^""]+""|(?-i:[a-z_][a-z0-9_.]*))\s*(=|\bTO\b))",
        RegexOptions.IgnoreCase)]
    private static partial Regex SessionSet();

    [GeneratedRegex(@"\bset_config\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex SetConfigCall();
}

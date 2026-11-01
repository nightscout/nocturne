using System.Text.RegularExpressions;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>
/// Keeps true what makes a lifted insert safe (see <see cref="HistoryClampLiftAllowlistTests"/>): an
/// insert by a clamped reader returns only the row it wrote and reads no other. An upsert, a trigger
/// or a default that reads a table would let it reach rows the clamp hides, and only the two files
/// that own the setting may write <c>app.history_clamped</c>.
/// </summary>
[Trait("Category", "Unit")]
public class HistoryClampLiftPremiseTests
{
    private static readonly IReadOnlyList<(string File, string Text)> Sources = ReadSources();

    private static readonly string[] SettingWriters =
    [
        "src/Infrastructure/Nocturne.Infrastructure.Data/Extensions/HistoryClampLiftExtensions.cs",
        "src/Infrastructure/Nocturne.Infrastructure.Data/Interceptors/TenantConnectionInterceptor.cs",
    ];

    /// <summary>
    /// Literals with an optional cast, numbers, booleans and the clock: none reads a row.
    /// </summary>
    private static readonly Regex SafeDefault = new(
        @"^\s*(CURRENT_TIMESTAMP|now\(\)|'[^']*'(::[a-z\[\]]+)?|-?\d+(\.\d+)?|true|false|NULL)\s*$",
        RegexOptions.IgnoreCase);

    /// <summary>
    /// Raw SQL upserts that never run lifted: they go through neither <c>SaveChangesAsync</c> nor
    /// <c>WriteWithHistoryClampLiftedAsync</c>, so they read under the caller's clamp.
    /// </summary>
    private static readonly string[] UnliftedUpserts =
    [
        // rejected_uploads is unclamped, and its upsert runs as a plain query.
        "src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/RejectedUploadRepository.cs",
        // owner_notices and instance_key_check are platform tables no policy covers, and their
        // inserts do nothing on conflict.
        "src/API/Nocturne.API/Services/Notifications/OwnerNoticeLedger.cs",
        "src/API/Nocturne.API/Services/Platform/InstanceKey/InstanceKeyCheck.cs",
        // job_ledger is a platform table no policy covers, and its upserts run as plain commands.
        "src/Infrastructure/Nocturne.Infrastructure.Data/Repositories/JobLedgerRepository.cs",
    ];

    [Fact]
    public void NoSqlUpsertsButTheUnliftedOnes()
    {
        Matching(new Regex(@"\bON\s+CONFLICT\b", RegexOptions.IgnoreCase))
            .Should().BeEquivalentTo(UnliftedUpserts,
                "an INSERT … ON CONFLICT DO UPDATE run lifted would write and return an existing row the clamp hides");
    }

    [Fact]
    public void NoTriggers()
    {
        Matching(new Regex(@"\bCREATE\s+(OR\s+REPLACE\s+)?(CONSTRAINT\s+)?TRIGGER\b", RegexOptions.IgnoreCase))
            .Should().BeEmpty("a trigger runs with the inserting statement's clamp lifted");
    }

    [Fact]
    public void OnlySessionTemporaryFunctions()
    {
        Matching(new Regex(@"\bCREATE\s+(OR\s+REPLACE\s+)?FUNCTION\s+(?!pg_temp\.)", RegexOptions.IgnoreCase))
            .Should().BeEmpty("a stored function could serve as a column default or a trigger body that reads other rows");
    }

    [Fact]
    public void ColumnDefaultsReadNoRows()
    {
        var defaults = new Regex(
            @"(?:HasDefaultValueSql|defaultValueSql:)\s*\(?\s*""((?:[^""\\]|\\.)*)""", RegexOptions.IgnoreCase);
        var rawDefaults = new Regex(@"\bDEFAULT\s+(?!PRIVILEGES\b|VALUES\b)([^,;""\r\n)]+(\([^)]*\))?)");

        var found = Sources
            .SelectMany(s => defaults.Matches(s.Text).Select(m => (s.File, Value: Regex.Unescape(m.Groups[1].Value)))
                .Concat(rawDefaults.Matches(s.Text).Select(m => (s.File, Value: m.Groups[1].Value))))
            .ToList();
        found.Should().Contain(d => d.Value == "CURRENT_TIMESTAMP", "the scan must see the model's defaults");

        var unsafeDefaults = found
            .Where(d => !SafeDefault.IsMatch(d.Value))
            .Select(d => $"{d.File}: {d.Value}")
            .Distinct()
            .ToList();

        unsafeDefaults.Should().BeEmpty("a default that calls a function or reads a table could return another row");
    }

    [Fact]
    public void OnlyTheOwnersWriteTheHistoryClampSetting()
    {
        var writes = new Regex(@"set_config|\bSET\s+(LOCAL\s+|SESSION\s+)?app\.history_clamped", RegexOptions.IgnoreCase);

        var writers = Sources
            .Where(s => s.Text.Contains("history_clamped", StringComparison.Ordinal) && writes.IsMatch(s.Text))
            .Select(s => s.File)
            .ToList();

        writers.Should().BeEquivalentTo(SettingWriters, "lifting the clamp goes through HistoryClampLiftExtensions");
    }

    [Theory]
    [InlineData("INSERT INTO x VALUES (1) ON CONFLICT (id) DO NOTHING", true)]
    [InlineData("the validation conflicts", false)]
    public void UpsertPatternMatchesSqlOnly(string text, bool matches)
    {
        new Regex(@"\bON\s+CONFLICT\b", RegexOptions.IgnoreCase).IsMatch(text).Should().Be(matches);
    }

    [Theory]
    [InlineData("CURRENT_TIMESTAMP", true)]
    [InlineData("now()", true)]
    [InlineData("'{}'::text[]", true)]
    [InlineData("'[]'::jsonb", true)]
    [InlineData("nextval('x')", false)]
    [InlineData("(SELECT max(id) FROM t)", false)]
    [InlineData("my_function()", false)]
    public void SafeDefaultsAreLiteralsOrTheClock(string value, bool safe)
    {
        SafeDefault.IsMatch(value).Should().Be(safe);
    }

    private static List<string> Matching(Regex pattern) =>
        Sources.Where(s => pattern.IsMatch(s.Text)).Select(s => s.File).ToList();

    /// <remarks>
    /// Migration Designer files are skipped: they record each migration's model and run no SQL, and
    /// the model snapshot carries the current defaults.
    /// </remarks>
    private static List<(string File, string Text)> ReadSources()
    {
        var root = RepositoryRoot();
        return Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && !path.EndsWith(".Designer.cs", StringComparison.Ordinal))
            .Select(path => (Path.GetRelativePath(root, path).Replace('\\', '/'), File.ReadAllText(path)))
            .ToList();
    }

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

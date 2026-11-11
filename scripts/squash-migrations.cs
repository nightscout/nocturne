// scripts/squash-migrations.cs
//
// Squashes every EF migration up to and including a release's last one into a single baseline:
// the script pg_dump takes of a database migrated that far. The baseline keeps that migration's
// id, so a database that applied it skips the baseline, and a fresh one runs the baseline then
// every later migration. The release becomes the oldest one the next release upgrades from;
// MigrationBaselineAttribute and DatabaseInitializationExtensions.EnsureReachesBaselineAsync
// refuse anything older.
//
// Squash to the last migration a release shipped, never to one only main has: an instance
// on that release must already hold the id the baseline reuses.
//
// Usage:
//   dotnet run scripts/squash-migrations.cs -- squash <migration-id> <release>
//   dotnet run scripts/squash-migrations.cs -- verify
//
//   squash   migrates a scratch database to <migration-id> with the current chain, writes the
//            baseline from its dump and deletes the migrations it replaces.
//   verify   migrates a scratch database to the baseline with the current chain and fails
//            unless its dump matches the baseline's script. Run it after squash.
//
// Example:
//   dotnet run scripts/squash-migrations.cs -- squash 20261026195407_AddReleaseNotices v0.3.0
//   dotnet run scripts/squash-migrations.cs -- verify
//
// Requires: .NET 10 SDK, Docker

#:project Shared/Shared.csproj
#:project ../src/Infrastructure/Nocturne.Infrastructure.Data.Migrations/Nocturne.Infrastructure.Data.Migrations.csproj
#:property PublishAot=false

#pragma warning disable IL2026, IL3050 // Script — never trimmed or AOT-compiled.

using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Interceptors;
using Npgsql;
using static ProcessHelpers;

const string PostgresImage = "postgres:17.6";
const string Database = "nocturne";
const string MigratorPassword = "squash-migrator";

var repoRoot = Directory.GetCurrentDirectory();
var migrationsDir = Path.Combine(repoRoot, "src", "Infrastructure", "Nocturne.Infrastructure.Data.Migrations", "Migrations");
var initScript = Path.Combine(repoRoot, "docs", "postgres", "container-init", "00-init.sh");

if (!Directory.Exists(migrationsDir) || !File.Exists(initScript))
{
    Console.Error.WriteLine("Run this from the repository root.");
    return 2;
}

switch (args)
{
    case ["squash", var floor, var release]:
        return await Squash(floor, release);
    case ["verify"]:
        return await Verify();
    default:
        Console.Error.WriteLine("Usage: dotnet run scripts/squash-migrations.cs -- squash <migration-id> <release> | verify");
        return 2;
}

async Task<int> Squash(string floor, string release)
{
    var timestamp = floor[..14];
    var designer = Directory.GetFiles(migrationsDir, "*.Designer.cs")
        .SingleOrDefault(f => DesignerId(File.ReadAllText(f)) == floor);
    if (designer is null)
    {
        Console.Error.WriteLine($"No migration in {migrationsDir} has the id {floor}.");
        return 1;
    }

    var dump = await DumpAt(floor);

    var designerText = File.ReadAllText(designer);
    var oldClass = Regex.Match(designerText, @"partial class (\w+)").Groups[1].Value;
    designerText = Regex.Replace(designerText, $@"partial class {oldClass}\b", "partial class Baseline");

    var replaced = Directory.GetFiles(migrationsDir)
        .Where(f => Regex.IsMatch(Path.GetFileName(f), @"^\d{14}_")
            && string.CompareOrdinal(Path.GetFileName(f)[..14], timestamp) <= 0)
        .ToList();
    foreach (var file in replaced)
        File.Delete(file);

    var stem = Path.Combine(migrationsDir, $"{timestamp}_Baseline");
    await File.WriteAllTextAsync(stem + ".sql", dump);
    await File.WriteAllTextAsync(stem + ".Designer.cs", designerText);
    await File.WriteAllTextAsync(stem + ".cs", BaselineSource(floor, release));

    Console.WriteLine($"[squash] Deleted {replaced.Count} files up to {floor}.");
    Console.WriteLine($"[squash] Wrote {timestamp}_Baseline (.cs, .Designer.cs, .sql: {dump.Length / 1024} KB) for {release}.");
    Console.WriteLine("[squash] Next: dotnet run scripts/squash-migrations.cs -- verify");
    return 0;
}

async Task<int> Verify()
{
    var script = Directory.GetFiles(migrationsDir, "*_Baseline.sql").Single();
    var designer = Path.ChangeExtension(script, ".Designer.cs");
    var baseline = DesignerId(File.ReadAllText(designer))
        ?? throw new InvalidOperationException($"{designer} has no [Migration] id.");

    var expected = (await File.ReadAllTextAsync(script)).Split('\n');
    var actual = (await DumpAt(baseline)).Split('\n');

    var missing = expected.Except(actual).ToList();
    var extra = actual.Except(expected).ToList();
    if (missing.Count == 0 && extra.Count == 0 && expected.SequenceEqual(actual))
    {
        Console.WriteLine($"[verify] A database migrated to {baseline} dumps identically to the baseline's script.");
        return 0;
    }

    Console.Error.WriteLine($"[verify] The dump differs from {Path.GetFileName(script)}.");
    foreach (var line in missing.Take(40))
        Console.Error.WriteLine($"  - {line}");
    foreach (var line in extra.Take(40))
        Console.Error.WriteLine($"  + {line}");
    if (missing.Count == 0 && extra.Count == 0)
        Console.Error.WriteLine("  (same lines, different order)");
    return 1;
}

/// <summary>
/// The cleaned dump of a scratch database migrated to <paramref name="target"/> by the current
/// chain, under the production role bootstrap.
/// </summary>
async Task<string> DumpAt(string target)
{
    var container = $"nocturne-squash-{Guid.NewGuid():N}"[..28];
    Run("docker", [
        "run", "-d", "--rm", "--name", container,
        "-e", "POSTGRES_PASSWORD=squash-super",
        "-e", $"POSTGRES_DB={Database}",
        "-e", $"NOCTURNE_MIGRATOR_PASSWORD={MigratorPassword}",
        "-e", "NOCTURNE_APP_PASSWORD=squash-app",
        "-e", "NOCTURNE_WEB_PASSWORD=squash-web",
        "--mount", $"type=bind,source={initScript},target=/docker-entrypoint-initdb.d/00-init.sh,readonly",
        "-p", "127.0.0.1::5432",
        PostgresImage,
    ]);

    try
    {
        var port = RunCapture("docker", ["port", container, "5432/tcp"]).Trim().Split('\n')[0].Split(':')[^1];
        var migrator = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = int.Parse(port),
            Database = Database,
            Username = "nocturne_migrator",
            Password = MigratorPassword,
            Pooling = false,
        }.ConnectionString;

        await WaitForConnectable(migrator);

        Console.WriteLine($"[squash] Migrating a scratch database to {target}...");
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(migrator, npgsql => npgsql
                .CommandTimeout((int)TimeSpan.FromHours(1).TotalSeconds)
                .UseNocturneMigrations())
            .AddInterceptors(new TenantConnectionInterceptor())
            .Options;
        await using (var context = new NocturneDbContext(options))
        {
            if (!context.Database.GetMigrations().Contains(target))
                throw new InvalidOperationException($"{target} is not a migration of the current chain.");
            await context.GetService<IMigrator>().MigrateAsync(target);
        }

        await RefuseObjectsTheCleanupDropsAsync(migrator);

        // Copied out as a file: captured stdout would be decoded with the console's code page.
        Console.WriteLine("[squash] Dumping...");
        Run("docker", [
            "exec", container, "pg_dump", "-U", "postgres", "-d", Database,
            "--no-owner", "--no-privileges",
            "--exclude-table=public.\"__EFMigrationsHistory\"",
            "--inserts", "--rows-per-insert=500",
            "-f", "/tmp/dump.sql",
        ]);
        var local = Path.Combine(Path.GetTempPath(), $"{container}.sql");
        Run("docker", ["cp", $"{container}:/tmp/dump.sql", local]);
        try
        {
            return Clean(await File.ReadAllTextAsync(local));
        }
        finally
        {
            File.Delete(local);
        }
    }
    finally
    {
        RunProcess("docker", ["rm", "-f", container]);
    }
}

/// <summary>
/// Waits out the image's init pass: its temporary server listens on no TCP port, and the roles do
/// not exist until the bootstrap script has run in it.
/// </summary>
static async Task WaitForConnectable(string connectionString)
{
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            return;
        }
        catch (NpgsqlException) when (attempt < 60)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }
}

/// <summary>
/// Fails on the objects <see cref="Clean"/> would break: a function needs
/// <c>check_function_bodies</c>, which the dropped <c>SET</c> lines carried, and an extension or
/// another schema needs privileges the migrator role does not have.
/// </summary>
static async Task RefuseObjectsTheCleanupDropsAsync(string connectionString)
{
    const string sql = """
        SELECT 'function ' || p.proname FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = 'public'
        UNION ALL SELECT 'trigger ' || tgname FROM pg_trigger WHERE NOT tgisinternal
        UNION ALL SELECT 'extension ' || extname FROM pg_extension WHERE extname <> 'plpgsql'
        UNION ALL SELECT 'schema ' || nspname FROM pg_namespace
            WHERE nspname NOT IN ('public', 'information_schema') AND nspname NOT LIKE 'pg\_%'
        """;

    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand(sql, connection);
    await using var reader = await command.ExecuteReaderAsync();

    var found = new List<string>();
    while (await reader.ReadAsync())
        found.Add(reader.GetString(0));

    if (found.Count > 0)
        throw new InvalidOperationException(
            $"The schema holds objects this script does not dump safely: {string.Join(", ", found)}.");
}

/// <summary>
/// The dump as one migration's SQL. EF runs it on its own connection, so session settings would
/// outlive it: <c>SET</c> lines go, and so does the <c>set_config</c> that empties
/// <c>search_path</c>, after which EF's unqualified history insert fails. The <c>\restrict</c>
/// lines pg_dump 17.6 adds are psql meta-commands, and comments only add size.
/// </summary>
static string Clean(string dump)
{
    var kept = dump.Replace("\r\n", "\n").Split('\n')
        .Where(line => !line.StartsWith("SET ", StringComparison.Ordinal)
            && !line.StartsWith("SELECT pg_catalog.set_config(", StringComparison.Ordinal)
            && !line.StartsWith('\\')
            && !line.StartsWith("--", StringComparison.Ordinal));

    return Regex.Replace(string.Join('\n', kept), @"\n{3,}", "\n\n").Trim() + "\n";
}

static string? DesignerId(string designer) =>
    Regex.Match(designer, @"\[Migration\(""([^""]+)""\)\]") is { Success: true } match ? match.Groups[1].Value : null;

static string BaselineSource(string floor, string release) => $$"""
    using Microsoft.EntityFrameworkCore.Migrations;

    #nullable disable

    namespace Nocturne.Infrastructure.Data.Migrations
    {
        /// <summary>
        /// Every migration up to and including {{floor}}, the last of {{release}},
        /// as the script pg_dump takes of a database migrated that far. Written by
        /// <c>scripts/squash-migrations.cs</c>; regenerate it rather than editing it.
        /// </summary>
        [MigrationBaseline("{{release}}")]
        public partial class Baseline : Migration
        {
            private const string Script = "Nocturne.Infrastructure.Data.Migrations.Baseline.sql";

            /// <inheritdoc />
            protected override void Up(MigrationBuilder migrationBuilder)
            {
                using var stream = typeof(Baseline).Assembly.GetManifestResourceStream(Script)
                    ?? throw new InvalidOperationException($"The embedded resource {Script} is missing.");
                using var reader = new StreamReader(stream);
                migrationBuilder.Sql(reader.ReadToEnd());
            }

            /// <inheritdoc />
            protected override void Down(MigrationBuilder migrationBuilder) =>
                throw new NotSupportedException("The baseline is the first migration; there is nothing before it to revert to.");
        }
    }

    """;

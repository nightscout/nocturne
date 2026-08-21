using System.ComponentModel.DataAnnotations.Schema;
using System.Reflection;

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

    /// <summary>Table names of every <see cref="ITenantScoped"/> entity, lowercased.</summary>
    public static IReadOnlySet<string> TenantScopedTableNames() =>
        typeof(ITenantScoped).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ITenantScoped).IsAssignableFrom(t))
            .Select(t => t.GetCustomAttribute<TableAttribute>()?.Name)
            .Where(n => n is not null)
            .Select(n => n!.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

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

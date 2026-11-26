using Xunit;

namespace Nocturne.Tests.Shared.Infrastructure;

/// <summary>
/// An xUnit class fixture holding one fully migrated database cloned from <see cref="SharedPostgres"/>.
/// A test class names it once: <c>public sealed class Database() : MigratedDatabase("team_staff");</c>.
/// A fixture that seeds data derives and extends <see cref="InitializeAsync"/>.
/// </summary>
/// <param name="prefix">Prefix of the database name. <see cref="SharedPostgres"/> appends a per-process sequence number, so two fixtures with one prefix still get separate databases.</param>
public class MigratedDatabase(string prefix) : IAsyncLifetime
{
    private TestDatabase _created = null!;

    public string Name => _created.Name;

    public string AppConnectionString => _created.AppConnectionString;

    public string MigratorConnectionString => _created.MigratorConnectionString;

    public string SuperuserConnectionString => _created.SuperuserConnectionString;

    public virtual async Task InitializeAsync() => _created = await SharedPostgres.CreateMigratedDatabaseAsync(prefix);

    public virtual Task DisposeAsync() => Task.CompletedTask;
}

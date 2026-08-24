namespace Nocturne.Infrastructure.Data.Tests;

/// <summary>
/// A <see cref="NocturneDbContext"/> for tests that only read model metadata.
/// </summary>
internal static class OfflineDbContext
{
    /// <summary>
    /// The Npgsql provider is required — table names, index filters and column types exist only on
    /// the relational mapping — but the model builds without a server, so nothing here is reachable
    /// and the connection is never opened.
    /// </summary>
    public static NocturneDbContext Create() =>
        new(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql("Host=localhost;Database=nocturne;Username=test;Password=test")
            .Options)
        { TenantId = Guid.NewGuid() };
}

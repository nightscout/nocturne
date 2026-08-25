namespace Nocturne.Infrastructure.Data.Tests;

/// <summary>
/// A <see cref="NocturneDbContext"/> for tests that only read model metadata.
/// </summary>
internal static class OfflineDbContext
{
    /// <summary>
    /// The Npgsql provider is required — table names, index filters and column types exist only on
    /// the relational mapping — but building the model opens no connection, so the host and
    /// credentials are placeholders for as long as a caller touches nothing but
    /// <see cref="DbContext.Model"/>.
    /// </summary>
    public static NocturneDbContext Create() =>
        new(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql("Host=localhost;Database=nocturne;Username=test;Password=test")
            .Options)
        { TenantId = Guid.NewGuid() };
}

using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Nocturne.Tests.Shared.Infrastructure;

/// <summary>
/// Shared test fixture that provides PostgreSQL container for integration tests
/// This fixture can be reused across multiple test assemblies to avoid the container management anti-pattern
/// </summary>
public class SharedTestContainerFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _postgreSqlContainer;

    public string PostgreSqlConnectionString { get; private set; } = string.Empty;
    public NpgsqlConnection Database { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        // Start PostgreSQL container
        _postgreSqlContainer = new PostgreSqlBuilder("postgres:16")
            .WithDatabase("nocturne_test")
            .WithUsername("postgres")
            .WithPassword("password")
            .WithPortBinding(5432, true)
            .Build();

        await _postgreSqlContainer.StartAsync();
        PostgreSqlConnectionString = _postgreSqlContainer.GetConnectionString();

        // Initialize PostgreSQL database
        Database = new NpgsqlConnection(PostgreSqlConnectionString);
        await Database.OpenAsync();

        // Create test tables and indexes
        await SetupTestTablesAsync();
    }

    public async Task DisposeAsync()
    {
        if (Database != null)
        {
            await Database.DisposeAsync();
        }

        if (_postgreSqlContainer != null)
        {
            await _postgreSqlContainer.StopAsync();
            await _postgreSqlContainer.DisposeAsync();
        }
    }

    private async Task SetupTestTablesAsync()
    {
        // Create basic test tables for integration testing
        // Note: In a full implementation, you would use EF Core migrations
        // or run the actual database migration scripts here

        var createTablesScript =
            @"
            CREATE TABLE IF NOT EXISTS entries (
                id VARCHAR(255) PRIMARY KEY,
                mills BIGINT,
                type VARCHAR(50),
                created_at TIMESTAMP
            );
            
            CREATE TABLE IF NOT EXISTS treatments (
                id VARCHAR(255) PRIMARY KEY,
                mills BIGINT,
                created_at TIMESTAMP
            );
            
            CREATE TABLE IF NOT EXISTS devicestatus (
                id VARCHAR(255) PRIMARY KEY,
                mills BIGINT,
                created_at TIMESTAMP
            );
            
            CREATE TABLE IF NOT EXISTS profiles (
                id VARCHAR(255) PRIMARY KEY,
                created_at TIMESTAMP
            );
            
            CREATE TABLE IF NOT EXISTS settings (
                id VARCHAR(255) PRIMARY KEY,
                key VARCHAR(255),
                value TEXT,
                created_at TIMESTAMP
            );
            
            -- Add indexes for performance
            CREATE INDEX IF NOT EXISTS idx_entries_mills ON entries(mills);
            CREATE INDEX IF NOT EXISTS idx_treatments_mills ON treatments(mills);
        ";

        using var command = new NpgsqlCommand(createTablesScript, Database);
        await command.ExecuteNonQueryAsync();
    }

    public async Task CleanupAsync()
    {
        // Clean up test data between tests
        var tables = new[] { "entries", "treatments", "devicestatus", "profiles", "settings" };

        foreach (var tableName in tables)
        {
            var deleteCommand = new NpgsqlCommand($"DELETE FROM {tableName}", Database);
            await deleteCommand.ExecuteNonQueryAsync();
        }
    }
}

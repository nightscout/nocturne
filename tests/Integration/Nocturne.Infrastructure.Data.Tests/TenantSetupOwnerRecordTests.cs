using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests;

/// <summary>A migrated database, connected as the migrator.</summary>
public class TenantSetupOwnerRecordFixture : IAsyncLifetime
{
    internal string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync() =>
        ConnectionString = (await SharedPostgres.CreateMigratedDatabaseAsync("setup_owner_record")).MigratorConnectionString;

    public Task DisposeAsync() => Task.CompletedTask;
}

/// <summary>
/// <c>tenants.setup_owner_subject_id</c> on the migrated schema: deleting the subject it names
/// ends the record and keeps the tenant.
/// </summary>
[Trait("Category", "Integration")]
public class TenantSetupOwnerRecordTests(TenantSetupOwnerRecordFixture fixture) : IClassFixture<TenantSetupOwnerRecordFixture>
{
    [Fact]
    public async Task Deleting_the_recorded_subject_sets_the_record_to_null()
    {
        var connectionString = fixture.ConnectionString;
        var tenant = Guid.CreateVersion7();
        var subject = Guid.CreateVersion7();

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO subjects (id, name) VALUES (@subject, 'Owner');
                INSERT INTO tenants (id, slug, display_name, is_active, setup_owner_subject_id)
                VALUES (@tenant, 'setup', 'Setup', true, @subject);
                DELETE FROM subjects WHERE id = @subject;
                """;
            cmd.Parameters.AddWithValue("subject", subject);
            cmd.Parameters.AddWithValue("tenant", tenant);
            await cmd.ExecuteNonQueryAsync();
        }

        await using var read = conn.CreateCommand();
        read.CommandText = "SELECT count(*), count(setup_owner_subject_id) FROM tenants WHERE id = @tenant";
        read.Parameters.AddWithValue("tenant", tenant);
        await using var reader = await read.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetInt64(0).Should().Be(1, "the tenant outlives the subject");
        reader.GetInt64(1).Should().Be(0, "the record ends with the subject");
    }
}

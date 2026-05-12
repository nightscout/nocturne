using Microsoft.EntityFrameworkCore;
using Npgsql;
using Nocturne.Infrastructure.Data;
using Testcontainers.PostgreSql;

namespace Nocturne.Infrastructure.Data.Performance.Tests.Infrastructure;

public class PostgresFixture : IAsyncDisposable
{
    private readonly PostgreSqlContainer _container;
    private string? _connectionString;
    private DbContextOptions<NocturneDbContext>? _options;

    public PostgresFixture()
    {
        _container = new PostgreSqlBuilder()
            .WithImage("postgres:17.6")
            .WithDatabase("nocturne_perf")
            .WithUsername("test")
            .WithPassword("test")
            .Build();
    }

    public bool IsInitialized => _options is not null;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        _connectionString = _container.GetConnectionString();

        _options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(_connectionString)
            .Options;

        // Create only the tables needed for benchmarks via raw SQL.
        // Full migrations require RLS roles (nocturne_migrator, nocturne_app)
        // that don't exist in the test container.
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = SchemaSql;
        await cmd.ExecuteNonQueryAsync();
    }

    public NocturneDbContext CreateContext()
    {
        if (_options is null)
            throw new InvalidOperationException("Call InitializeAsync before CreateContext");
        return new NocturneDbContext(_options);
    }

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
    }

    /// <summary>
    /// Minimal schema for benchmark tables. Matches the EF model closely enough
    /// for LINQ queries to translate correctly, without needing full migrations.
    /// </summary>
    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS tenants (
            id uuid PRIMARY KEY,
            name text,
            slug text,
            api_secret_hash text,
            created_at timestamptz NOT NULL DEFAULT now(),
            updated_at timestamptz NOT NULL DEFAULT now(),
            enable text,
            time_zone text,
            custom_title text,
            default_role text
        );

        CREATE TABLE IF NOT EXISTS sensor_glucose (
            id uuid PRIMARY KEY,
            tenant_id uuid NOT NULL,
            timestamp timestamptz NOT NULL,
            mgdl double precision NOT NULL,
            direction text,
            trend_rate double precision,
            noise double precision,
            filtered double precision,
            unfiltered double precision,
            delta double precision,
            glucose_processing text,
            smoothed_mgdl double precision,
            unsmoothed_mgdl double precision,
            device text,
            app text,
            data_source text,
            sync_identifier text,
            utc_offset integer,
            correlation_id uuid,
            patient_device_id uuid,
            legacy_id text,
            additional_properties jsonb,
            sys_created_at timestamptz NOT NULL DEFAULT now(),
            sys_updated_at timestamptz NOT NULL DEFAULT now()
        );

        CREATE INDEX ix_sensor_glucose_timestamp ON sensor_glucose (timestamp DESC);
        CREATE INDEX ix_sensor_glucose_tenant_timestamp ON sensor_glucose (tenant_id, timestamp DESC);
        CREATE INDEX ix_sensor_glucose_correlation_id ON sensor_glucose (correlation_id);

        CREATE TABLE IF NOT EXISTS boluses (
            id uuid PRIMARY KEY,
            tenant_id uuid NOT NULL,
            timestamp timestamptz NOT NULL,
            insulin double precision NOT NULL,
            bolus_type text,
            bolus_kind text,
            automatic boolean NOT NULL DEFAULT false,
            device text,
            app text,
            data_source text,
            sync_identifier text,
            utc_offset integer,
            correlation_id uuid,
            legacy_id text,
            additional_properties jsonb,
            sys_created_at timestamptz NOT NULL DEFAULT now(),
            sys_updated_at timestamptz NOT NULL DEFAULT now()
        );

        CREATE INDEX ix_boluses_timestamp ON boluses (timestamp DESC);
        CREATE INDEX ix_boluses_tenant_timestamp ON boluses (tenant_id, timestamp DESC);
        CREATE INDEX ix_boluses_correlation_id ON boluses (correlation_id);

        CREATE TABLE IF NOT EXISTS linked_records (
            id uuid PRIMARY KEY,
            tenant_id uuid NOT NULL,
            canonical_id uuid NOT NULL,
            record_type varchar(20) NOT NULL,
            record_id uuid NOT NULL,
            source_timestamp bigint NOT NULL,
            data_source varchar(100),
            is_primary boolean NOT NULL DEFAULT false,
            sys_created_at timestamptz NOT NULL DEFAULT now()
        );

        CREATE INDEX ix_linked_records_canonical ON linked_records (canonical_id);
        CREATE INDEX ix_linked_records_record ON linked_records (record_type, record_id);
        CREATE UNIQUE INDEX ix_linked_records_tenant_type_id ON linked_records (tenant_id, record_type, record_id);
        CREATE INDEX ix_linked_records_type_canonical_primary ON linked_records (record_type, canonical_id, is_primary);
        CREATE INDEX ix_linked_records_type_timestamp ON linked_records (record_type, source_timestamp);
        CREATE INDEX ix_linked_records_non_primary_record ON linked_records (record_type, record_id) WHERE NOT is_primary;
        """;
}

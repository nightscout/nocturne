using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Infrastructure.Data.Configuration;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Tests.Rls;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.StorageParameters;

/// <summary>
/// Asserts, against a real PostgreSQL, what the startup reconciler leaves in <c>pg_class.reloptions</c>:
/// every tenant-scoped table carries the pinned <c>autovacuum_analyze_scale_factor</c>, a run over an
/// already-pinned database alters nothing, and a value changed or reset by hand is put back. The
/// expected table set comes from the fixture, which walks <see cref="ITenantScoped"/> CLR types
/// rather than asking the reconciler what it was told to do.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class TenantTableStorageParameterTests
{
    private readonly RlsCompletenessFixture _fx;

    // One tenant-scoped table to disturb by hand; the collection runs its tests sequentially,
    // and each test that disturbs it ends with the reconciled state restored.
    private const string ProbeTable = "boluses";

    public TenantTableStorageParameterTests(RlsCompletenessFixture fx) => _fx = fx;

    [Fact]
    public async Task EveryTenantScopedTable_CarriesThePinnedAnalyzeScaleFactor()
    {
        _fx.TenantScopedTableNames.Should().NotBeEmpty();

        var stored = await ReadStoredValuesAsync();

        foreach (var table in _fx.TenantScopedTableNames)
        {
            stored.Should().ContainKey(table, $"{table} is a tenant-scoped table and must exist");
            stored[table].Should().Be(TenantTableStorageParameters.AnalyzeScaleFactor,
                $"{table} must carry the pinned {TenantTableStorageParameters.AnalyzeScaleFactorName}");
        }
    }

    [Fact]
    public void TableSet_CoversTheTablesTheProductionMeasurementWasTakenOn()
    {
        // The reconciler derives its set from the model; if either of these ever stopped being
        // tenant-scoped the fix would silently no longer cover the tables it was written for.
        _fx.TenantScopedTableNames.Should().Contain(["linked_records", "sensor_glucose"]);
    }

    [Fact]
    public async Task Reconcile_OverAPinnedDatabase_AltersNothing()
    {
        var altered = await ReconcileAsync();

        altered.Should().Be(0, "a steady-state startup must issue no DDL");
    }

    [Fact]
    public async Task Reconcile_RestoresAValueChangedByHand()
    {
        await ExecuteAsMigratorAsync(
            $"ALTER TABLE {ProbeTable} SET ({TenantTableStorageParameters.AnalyzeScaleFactorName} = 0.2)");
        (await ReadStoredValuesAsync())[ProbeTable].Should().Be("0.2");

        var altered = await ReconcileAsync();

        altered.Should().Be(1);
        (await ReadStoredValuesAsync())[ProbeTable].Should().Be(TenantTableStorageParameters.AnalyzeScaleFactor);
    }

    [Fact]
    public async Task Reconcile_RestoresAValueResetByHand()
    {
        await ExecuteAsMigratorAsync(
            $"ALTER TABLE {ProbeTable} RESET ({TenantTableStorageParameters.AnalyzeScaleFactorName})");
        (await ReadStoredValuesAsync())[ProbeTable].Should().BeNull("RESET removes the parameter entirely");

        var altered = await ReconcileAsync();

        altered.Should().Be(1);
        (await ReadStoredValuesAsync())[ProbeTable].Should().Be(TenantTableStorageParameters.AnalyzeScaleFactor);
    }

    [Fact]
    public async Task Reconcile_LeavesOtherStorageParametersAlone()
    {
        // An operator's own tuning on a different parameter must survive the reconcile; only the
        // parameter Nocturne owns is written.
        await ExecuteAsMigratorAsync($"ALTER TABLE {ProbeTable} SET (autovacuum_vacuum_scale_factor = 0.05)");
        await ExecuteAsMigratorAsync(
            $"ALTER TABLE {ProbeTable} RESET ({TenantTableStorageParameters.AnalyzeScaleFactorName})");
        try
        {
            (await ReconcileAsync()).Should().Be(1);

            (await ReadStoredValuesAsync())[ProbeTable].Should().Be(TenantTableStorageParameters.AnalyzeScaleFactor);
            (await ReadStoredValueAsync(ProbeTable, "autovacuum_vacuum_scale_factor")).Should().Be("0.05");
        }
        finally
        {
            await ExecuteAsMigratorAsync($"ALTER TABLE {ProbeTable} RESET (autovacuum_vacuum_scale_factor)");
        }
    }

    private Task<int> ReconcileAsync() =>
        DatabaseInitializationExtensions.ReconcileTenantTableStorageParametersAsync(
            _fx.MigratorConnectionString, NullLogger.Instance);

    private async Task ExecuteAsMigratorAsync(string sql)
    {
        await using var conn = await _fx.OpenMigratorConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Every public table's stored analyze scale factor, or <c>null</c> when absent.</summary>
    private Task<Dictionary<string, string?>> ReadStoredValuesAsync() =>
        ReadStoredValuesAsync(TenantTableStorageParameters.AnalyzeScaleFactorName);

    private async Task<string?> ReadStoredValueAsync(string table, string parameter) =>
        (await ReadStoredValuesAsync(parameter))[table];

    private async Task<Dictionary<string, string?>> ReadStoredValuesAsync(string parameter)
    {
        await using var conn = await _fx.OpenMigratorConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT c.relname::text,
                   (SELECT o.option_value
                    FROM pg_options_to_table(c.reloptions) o
                    WHERE o.option_name = $1)
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relkind = 'r'
            """, conn);
        cmd.Parameters.AddWithValue(parameter);

        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
        return result;
    }
}

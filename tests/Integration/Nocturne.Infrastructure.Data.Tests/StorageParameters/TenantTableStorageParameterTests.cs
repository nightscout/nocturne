using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Infrastructure.Data.Configuration;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Tests.Rls;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.StorageParameters;

/// <summary>
/// Asserts, against a real PostgreSQL, what the startup reconciler leaves in <c>pg_class.reloptions</c>:
/// every tenant-scoped table carries the <c>autovacuum_analyze_scale_factor</c> ceiling, a run over an
/// already-reconciled database alters nothing, a value raised or reset by hand is put back, a value
/// lowered by hand is kept, and a table whose lock is held is skipped rather than stalling. The
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
    public async Task EveryTenantScopedTable_CarriesTheAnalyzeScaleFactorCeiling()
    {
        _fx.TenantScopedTableNames.Should().NotBeEmpty();

        var stored = await ReadStoredValuesAsync();

        foreach (var table in _fx.TenantScopedTableNames)
        {
            stored.Should().ContainKey(table, $"{table} is a tenant-scoped table and must exist");
            stored[table].Should().Be(TenantTableStorageParameters.AnalyzeScaleFactor,
                $"{table} must carry the {TenantTableStorageParameters.AnalyzeScaleFactorName} ceiling");
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
    public async Task Reconcile_OverAReconciledDatabase_AltersNothing()
    {
        var altered = await ReconcileAsync();

        altered.Should().Be(0, "a steady-state startup must issue no DDL");
    }

    [Fact]
    public async Task Reconcile_LowersAValueRaisedByHand()
    {
        await ExecuteAsMigratorAsync(
            $"ALTER TABLE {ProbeTable} SET ({TenantTableStorageParameters.AnalyzeScaleFactorName} = 0.2)");
        (await ReadStoredValuesAsync())[ProbeTable].Should().Be("0.2");

        var altered = await ReconcileAsync();

        altered.Should().Be(1);
        (await ReadStoredValuesAsync())[ProbeTable].Should().Be(TenantTableStorageParameters.AnalyzeScaleFactor);
    }

    [Fact]
    public async Task Reconcile_KeepsAStricterValueSetByHand()
    {
        await ExecuteAsMigratorAsync(
            $"ALTER TABLE {ProbeTable} SET ({TenantTableStorageParameters.AnalyzeScaleFactorName} = 0.005)");
        try
        {
            var altered = await ReconcileAsync();

            altered.Should().Be(0, "an operator's lower value is under the ceiling and must be kept");
            (await ReadStoredValuesAsync())[ProbeTable].Should().Be("0.005");
        }
        finally
        {
            await RestoreProbeTableAsync();
        }
    }

    [Fact]
    public async Task Reconcile_SkipsATableWhoseLockIsHeld_AndAltersItOnceReleased()
    {
        await ExecuteAsMigratorAsync(
            $"ALTER TABLE {ProbeTable} RESET ({TenantTableStorageParameters.AnalyzeScaleFactorName})");

        // ALTER TABLE … SET needs SHARE UPDATE EXCLUSIVE, which is self-conflicting; holding it from
        // another session stands in for an autovacuum of the table.
        await using var holder = await _fx.OpenMigratorConnectionAsync();
        await using var holding = await holder.BeginTransactionAsync();
        await using (var lockCmd = new NpgsqlCommand($"LOCK TABLE {ProbeTable} IN SHARE UPDATE EXCLUSIVE MODE", holder, holding))
            await lockCmd.ExecuteNonQueryAsync();

        var whileHeld = await ReconcileAsync();

        whileHeld.Should().Be(0, "the lock_timeout must skip the table instead of waiting or throwing");
        (await ReadStoredValuesAsync())[ProbeTable].Should().BeNull();

        await holding.RollbackAsync();

        (await ReconcileAsync()).Should().Be(1, "the skipped table is picked up on the next run");
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
            await RestoreProbeTableAsync();
        }
    }

    /// <summary>
    /// Puts the probe table back to the reconciled state whatever a test left behind: a value
    /// under the ceiling would otherwise survive the next reconcile and leak into the other tests.
    /// </summary>
    private async Task RestoreProbeTableAsync()
    {
        await ExecuteAsMigratorAsync(
            $"ALTER TABLE {ProbeTable} RESET ({TenantTableStorageParameters.AnalyzeScaleFactorName})");
        await ReconcileAsync();
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

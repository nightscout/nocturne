using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Infrastructure.Data.Configuration;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Tests.Rls;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.StorageParameters;

/// <summary>
/// Asserts, against a real PostgreSQL, what the startup reconciler leaves in <c>pg_class.reloptions</c>:
/// every tenant-scoped table carries the analyze and vacuum scale factor ceilings, a run over an
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

    public static TheoryData<string> ParameterNames() =>
        new(TenantTableStorageParameters.All.Select(p => p.Name));

    [Fact]
    public async Task EveryTenantScopedTable_CarriesEveryCeiling()
    {
        _fx.TenantScopedTableNames.Should().NotBeEmpty();

        foreach (var parameter in TenantTableStorageParameters.All)
        {
            var stored = await ReadStoredValuesAsync(parameter.Name);

            foreach (var table in _fx.TenantScopedTableNames)
            {
                stored.Should().ContainKey(table, $"{table} is a tenant-scoped table and must exist");
                stored[table].Should().Be(parameter.Ceiling,
                    $"{table} must carry the {parameter.Name} ceiling");
            }
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

    [Theory]
    [MemberData(nameof(ParameterNames))]
    public async Task Reconcile_LowersAValueRaisedByHand(string parameter)
    {
        await ExecuteAsMigratorAsync($"ALTER TABLE {ProbeTable} SET ({parameter} = 0.2)");
        (await ReadStoredValueAsync(ProbeTable, parameter)).Should().Be("0.2");

        var altered = await ReconcileAsync();

        altered.Should().Be(1);
        (await ReadStoredValueAsync(ProbeTable, parameter)).Should().Be(CeilingOf(parameter));
    }

    [Theory]
    [InlineData("autovacuum_analyze_scale_factor", "0.005")]
    [InlineData("autovacuum_vacuum_scale_factor", "0.01")]
    public async Task Reconcile_KeepsAStricterValueSetByHand(string parameter, string stricter)
    {
        await ExecuteAsMigratorAsync($"ALTER TABLE {ProbeTable} SET ({parameter} = {stricter})");
        try
        {
            var altered = await ReconcileAsync();

            altered.Should().Be(0, "an operator's lower value is under the ceiling and must be kept");
            (await ReadStoredValueAsync(ProbeTable, parameter)).Should().Be(stricter);
        }
        finally
        {
            await RestoreProbeTableAsync();
        }
    }

    [Fact]
    public async Task Reconcile_WithOnlyTheVacuumFactorDrifted_LeavesTheAnalyzeFactorAlone()
    {
        var analyze = TenantTableStorageParameters.AnalyzeScaleFactor.Name;
        var vacuum = TenantTableStorageParameters.VacuumScaleFactor.Name;
        await ExecuteAsMigratorAsync($"ALTER TABLE {ProbeTable} SET ({analyze} = 0.005)");
        await ExecuteAsMigratorAsync($"ALTER TABLE {ProbeTable} RESET ({vacuum})");
        try
        {
            (await ReconcileAsync()).Should().Be(1);

            (await ReadStoredValueAsync(ProbeTable, vacuum))
                .Should().Be(TenantTableStorageParameters.VacuumScaleFactor.Ceiling);
            (await ReadStoredValueAsync(ProbeTable, analyze)).Should().Be("0.005");
        }
        finally
        {
            await RestoreProbeTableAsync();
        }
    }

    [Fact]
    public async Task Reconcile_SkipsATableWhoseLockIsHeld_AndAltersItOnceReleased()
    {
        await ResetProbeTableAsync();

        // ALTER TABLE … SET needs SHARE UPDATE EXCLUSIVE, which is self-conflicting; holding it from
        // another session stands in for an autovacuum of the table.
        await using var holder = await _fx.OpenMigratorConnectionAsync();
        await using var holding = await holder.BeginTransactionAsync();
        await using (var lockCmd = new NpgsqlCommand($"LOCK TABLE {ProbeTable} IN SHARE UPDATE EXCLUSIVE MODE", holder, holding))
            await lockCmd.ExecuteNonQueryAsync();

        var whileHeld = await ReconcileAsync();

        whileHeld.Should().Be(0, "the lock_timeout must skip the table instead of waiting or throwing");
        foreach (var parameter in TenantTableStorageParameters.All)
            (await ReadStoredValueAsync(ProbeTable, parameter.Name)).Should().BeNull();

        await holding.RollbackAsync();

        (await ReconcileAsync()).Should().Be(1, "the skipped table is picked up on the next run");
        foreach (var parameter in TenantTableStorageParameters.All)
            (await ReadStoredValueAsync(ProbeTable, parameter.Name)).Should().Be(parameter.Ceiling);
    }

    [Fact]
    public async Task Reconcile_RestoresValuesResetByHand()
    {
        await ResetProbeTableAsync();
        foreach (var parameter in TenantTableStorageParameters.All)
            (await ReadStoredValueAsync(ProbeTable, parameter.Name)).Should().BeNull("RESET removes the parameter entirely");

        var altered = await ReconcileAsync();

        altered.Should().Be(1, "both parameters are set by one statement per table");
        foreach (var parameter in TenantTableStorageParameters.All)
            (await ReadStoredValueAsync(ProbeTable, parameter.Name)).Should().Be(parameter.Ceiling);
    }

    [Fact]
    public async Task Reconcile_LeavesOtherStorageParametersAlone()
    {
        // An operator's own tuning on a parameter Nocturne does not own must survive the reconcile.
        await ExecuteAsMigratorAsync($"ALTER TABLE {ProbeTable} SET (autovacuum_vacuum_cost_limit = 500)");
        await ResetProbeTableAsync();
        try
        {
            (await ReconcileAsync()).Should().Be(1);

            (await ReadStoredValueAsync(ProbeTable, "autovacuum_vacuum_cost_limit")).Should().Be("500");
        }
        finally
        {
            await ExecuteAsMigratorAsync($"ALTER TABLE {ProbeTable} RESET (autovacuum_vacuum_cost_limit)");
            await RestoreProbeTableAsync();
        }
    }

    private static string CeilingOf(string parameter) =>
        TenantTableStorageParameters.All.Single(p => p.Name == parameter).Ceiling;

    private Task ResetProbeTableAsync() =>
        ExecuteAsMigratorAsync(
            $"ALTER TABLE {ProbeTable} RESET ({string.Join(", ", TenantTableStorageParameters.All.Select(p => p.Name))})");

    /// <summary>
    /// Puts the probe table back to the reconciled state whatever a test left behind: a value
    /// under the ceiling would otherwise survive the next reconcile and leak into the other tests.
    /// </summary>
    private async Task RestoreProbeTableAsync()
    {
        await ResetProbeTableAsync();
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

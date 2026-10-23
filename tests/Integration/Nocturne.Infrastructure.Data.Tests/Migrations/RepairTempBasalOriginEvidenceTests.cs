using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Runs <c>RepairTempBasalOriginEvidence</c> over temp basals in each state the corroborator left
/// them in, in two tenants, then down and up again, on a real PostgreSQL.
/// </summary>
public class RepairTempBasalOriginEvidenceFixture : IAsyncLifetime
{
    private const string PriorMigration = "20261021233224_AddTempBasalOriginPendingIndex";

    internal static readonly DateTime SeededAt = new(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc);

    internal static readonly Guid TenantA = Guid.Parse("a1111111-1111-7111-8111-111111112405");
    internal static readonly Guid TenantB = Guid.Parse("b2222222-2222-7222-8222-222222222405");

    /// <summary>A Tidepool-mirrored Manual temp classified Reported from no evidence.</summary>
    internal static readonly Guid Tidepool = Guid.Parse("70000000-0000-7000-8000-000000002401");

    /// <summary>A Manual temp with no device classified Reported from no evidence.</summary>
    internal static readonly Guid Deviceless = Guid.Parse("70000000-0000-7000-8000-000000002402");

    /// <summary>A temp judged with no snapshot to judge it by.</summary>
    internal static readonly Guid Uncorroborated = Guid.Parse("70000000-0000-7000-8000-000000002403");

    /// <summary>Tenant B's temp judged with no snapshot to judge it by.</summary>
    internal static readonly Guid UncorroboratedInB = Guid.Parse("70000000-0000-7000-8000-000000002404");

    /// <summary>A Manual temp CareLink wrote before it kept the flag.</summary>
    internal static readonly Guid CareLink = Guid.Parse("70000000-0000-7000-8000-000000002405");

    /// <summary>A Manual temp a user stated through V4, which keeps the flag.</summary>
    internal static readonly Guid UserStated = Guid.Parse("70000000-0000-7000-8000-000000002406");

    /// <summary>A Trio Manual temp marked Reported, which the classification never did without a flag.</summary>
    internal static readonly Guid TrioReported = Guid.Parse("70000000-0000-7000-8000-000000002407");

    /// <summary>A temp a snapshot judged.</summary>
    internal static readonly Guid Judged = Guid.Parse("70000000-0000-7000-8000-000000002408");

    /// <summary>A Tidepool-mirrored temp not yet classified.</summary>
    internal static readonly Guid Unclassified = Guid.Parse("70000000-0000-7000-8000-000000002409");

    /// <summary>
    /// A Manual temp a user stated through V4 before the flag was kept: no flag, no uploader, and no
    /// legacy id, since a V4 client's rows never carry one.
    /// </summary>
    internal static readonly Guid V4Stated = Guid.Parse("70000000-0000-7000-8000-000000002410");

    /// <summary>A soft-deleted temp judged with no snapshot to judge it by.</summary>
    internal static readonly Guid DeletedUncorroborated = Guid.Parse("70000000-0000-7000-8000-000000002411");

    /// <summary>A Manual temp classified Reported whose flag is not a boolean, which reads as no flag.</summary>
    internal static readonly Guid NonBooleanFlag = Guid.Parse("70000000-0000-7000-8000-000000002412");

    /// <summary>A Manual temp classified Reported whose flag is a boolean held as a string.</summary>
    internal static readonly Guid StringBooleanFlag = Guid.Parse("70000000-0000-7000-8000-000000002413");

    internal sealed record Row(string Origin, string? Evidence, DateTime UpdatedAt);

    internal Dictionary<Guid, Row> Seeded { get; } = [];
    internal Dictionary<Guid, Row> AfterUp { get; } = [];
    internal Dictionary<Guid, Row> AfterSecondUp { get; } = [];

    private string _migrator = string.Empty;

    public async Task InitializeAsync()
    {
        var database = await SharedPostgres.CreateEmptyDatabaseAsync("repair_temp_basal_origin_evidence");
        _migrator = database.MigratorConnectionString;

        await MigrateToAsync(PriorMigration);
        await SeedAsync();
        await ReadAsync(Seeded);
        await MigrateToAsync(null);
        await ReadAsync(AfterUp);
        await MigrateToAsync(PriorMigration);
        await MigrateToAsync(null);
        await ReadAsync(AfterSecondUp);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task MigrateToAsync(string? target)
    {
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(_migrator, npgsql => npgsql.UseNocturneMigrations())
            .AddInterceptors(new TenantConnectionInterceptor())
            .Options;
        await using var context = new NocturneDbContext(options);
        await context.GetService<IMigrator>().MigrateAsync(target);
    }

    private async Task SeedAsync()
    {
        await using var conn = new NpgsqlConnection(_migrator);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $$"""
            INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at) VALUES
              ('{{TenantA}}', 'tenant-a', 'Tenant A', true, now(), now()),
              ('{{TenantB}}', 'tenant-b', 'Tenant B', true, now(), now());

            SELECT set_config('app.current_tenant_id', '{{TenantA}}', false);
            INSERT INTO temp_basals
              (id, tenant_id, start_timestamp, rate, device, app, data_source, origin, origin_evidence, additional_properties, legacy_id, sys_updated_at)
            VALUES
              ('{{Tidepool}}', '{{TenantA}}', '2026-08-01T01:00:00Z', 0.85, 'tidepool', 'tidepool', 'nightscout-connector', 'Manual', 'Reported', NULL, 'tp-1', '{{SeededAt:O}}'),
              ('{{Deviceless}}', '{{TenantA}}', '2026-08-01T02:00:00Z', 1.1, NULL, NULL, 'nightscout-connector', 'Manual', 'Reported', NULL, 'ns-1', '{{SeededAt:O}}'),
              ('{{Uncorroborated}}', '{{TenantA}}', '2026-08-01T03:00:00Z', 1.0, 'Trio', 'Trio', NULL, 'Algorithm', 'Uncorroborated', NULL, 'trio-1', '{{SeededAt:O}}'),
              ('{{CareLink}}', '{{TenantA}}', '2026-08-01T04:00:00Z', 0.6, NULL, NULL, 'carelink-connector', 'Manual', 'Reported', NULL, NULL, '{{SeededAt:O}}'),
              ('{{UserStated}}', '{{TenantA}}', '2026-08-01T05:00:00Z', 0.7, NULL, NULL, NULL, 'Manual', 'Reported', '{"automatic": false}', NULL, '{{SeededAt:O}}'),
              ('{{V4Stated}}', '{{TenantA}}', '2026-08-01T09:00:00Z', 0.4, NULL, NULL, NULL, 'Manual', 'Reported', NULL, NULL, '{{SeededAt:O}}'),
              ('{{TrioReported}}', '{{TenantA}}', '2026-08-01T06:00:00Z', 0.8, 'Trio', 'Trio', NULL, 'Manual', 'Reported', NULL, 'trio-2', '{{SeededAt:O}}'),
              ('{{Judged}}', '{{TenantA}}', '2026-08-01T07:00:00Z', 0.9, NULL, NULL, 'nightscout-connector', 'Manual', 'Snapshot', NULL, 'ns-2', '{{SeededAt:O}}'),
              ('{{Unclassified}}', '{{TenantA}}', '2026-08-01T08:00:00Z', 0.5, 'tidepool', 'tidepool', 'nightscout-connector', 'Manual', NULL, NULL, 'tp-2', '{{SeededAt:O}}'),
              ('{{NonBooleanFlag}}', '{{TenantA}}', '2026-08-01T10:00:00Z', 0.3, NULL, NULL, 'nightscout-connector', 'Manual', 'Reported', '{"automatic": "yes"}', 'ns-3', '{{SeededAt:O}}'),
              ('{{StringBooleanFlag}}', '{{TenantA}}', '2026-08-01T11:00:00Z', 0.3, NULL, NULL, 'nightscout-connector', 'Manual', 'Reported', '{"automatic": "False"}', 'ns-4', '{{SeededAt:O}}');
            INSERT INTO temp_basals (id, tenant_id, start_timestamp, rate, origin, origin_evidence, legacy_id, deleted_at, sys_updated_at)
            VALUES ('{{DeletedUncorroborated}}', '{{TenantA}}', '2026-08-01T12:00:00Z', 1.0, 'Algorithm', 'Uncorroborated', 'trio-3', '{{SeededAt:O}}', '{{SeededAt:O}}');

            SELECT set_config('app.current_tenant_id', '{{TenantB}}', false);
            INSERT INTO temp_basals (id, tenant_id, start_timestamp, rate, device, app, origin, origin_evidence, sys_updated_at)
            VALUES ('{{UncorroboratedInB}}', '{{TenantB}}', '2026-08-01T03:00:00Z', 1.0, NULL, NULL, 'Manual', 'Uncorroborated', '{{SeededAt:O}}');

            SELECT set_config('app.current_tenant_id', '', false);
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task ReadAsync(Dictionary<Guid, Row> into)
    {
        await using var conn = new NpgsqlConnection(_migrator);
        await conn.OpenAsync();
        foreach (var tenant in new[] { TenantA, TenantB })
        {
            await using (var pin = conn.CreateCommand())
            {
                pin.CommandText = $"SELECT set_config('app.current_tenant_id', '{tenant}', false);";
                await pin.ExecuteNonQueryAsync();
            }

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, origin, origin_evidence, sys_updated_at FROM temp_basals;";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                into[reader.GetGuid(0)] = new Row(
                    reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetDateTime(3));
        }
    }
}

/// <inheritdoc cref="RepairTempBasalOriginEvidenceFixture"/>
[Trait("Category", "Integration")]
public class RepairTempBasalOriginEvidenceTests(RepairTempBasalOriginEvidenceFixture fixture)
    : IClassFixture<RepairTempBasalOriginEvidenceFixture>
{
    private Dictionary<Guid, RepairTempBasalOriginEvidenceFixture.Row> Rows => fixture.AfterUp;

    [Theory]
    [InlineData("70000000-0000-7000-8000-000000002401")]
    [InlineData("70000000-0000-7000-8000-000000002402")]
    [InlineData("70000000-0000-7000-8000-000000002412")]
    public void A_manual_temp_classified_reported_from_no_evidence_is_classified_again(string id)
    {
        Rows[Guid.Parse(id)].Should().BeEquivalentTo(new { Origin = "Manual", Evidence = (string?)null });
    }

    [Theory]
    [InlineData("70000000-0000-7000-8000-000000002403", "Algorithm")]
    [InlineData("70000000-0000-7000-8000-000000002404", "Manual")]
    [InlineData("70000000-0000-7000-8000-000000002411", "Algorithm")]
    public void An_uncorroborated_temp_in_every_tenant_deleted_or_not_is_judged_again(string id, string origin)
    {
        Rows[Guid.Parse(id)].Should().BeEquivalentTo(new { Origin = origin, Evidence = "Uploader" });
    }

    [Theory]
    [InlineData("70000000-0000-7000-8000-000000002405")]
    [InlineData("70000000-0000-7000-8000-000000002406")]
    [InlineData("70000000-0000-7000-8000-000000002407")]
    [InlineData("70000000-0000-7000-8000-000000002408")]
    [InlineData("70000000-0000-7000-8000-000000002409")]
    [InlineData("70000000-0000-7000-8000-000000002410")]
    [InlineData("70000000-0000-7000-8000-000000002413")]
    public void A_statement_a_judgement_or_an_unclassified_row_is_left_as_it_is(string id)
    {
        Rows[Guid.Parse(id)].Should().Be(fixture.Seeded[Guid.Parse(id)]);
    }

    [Fact]
    public void No_row_is_stamped_as_updated()
    {
        Rows.Values.Should().OnlyContain(r => r.UpdatedAt == RepairTempBasalOriginEvidenceFixture.SeededAt);
    }

    [Fact]
    public void Running_it_again_changes_nothing_more()
    {
        fixture.AfterSecondUp.Should().BeEquivalentTo(fixture.AfterUp);
    }
}

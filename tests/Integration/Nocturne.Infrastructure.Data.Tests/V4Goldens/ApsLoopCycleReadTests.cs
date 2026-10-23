using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.V4Goldens;

[Trait("Category", "Integration")]
[Collection("V4 goldens")]
public class ApsLoopCycleReadTests(V4GoldenFixture fixture)
{
    private static readonly DateTime Start = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);

    private const string CappedReason =
        "COB: 0, Dev: 12, BGI: -1, ISF: 50, CR: 10, Target: 100; Eventual BG 180 >= 100, insulinReq 0.9. "
        + "Microbolusing 0.4U. adj. req. rate: 3.2 to maxSafeBasal: 2.4, temp 1.2 < 2.4U/hr.";

    [Fact]
    public async Task Reads_each_cycle_and_the_nearest_beyond_each_end()
    {
        var tenant = Guid.NewGuid();
        using var scope = await fixture.BeginTenantScopeAsync(tenant);
        var repo = scope.ServiceProvider.GetRequiredService<IApsSnapshotRepository>();
        await SeedAsync(repo);

        var cycles = await repo.GetLoopCyclesAsync(Start, Start.AddMinutes(20));

        cycles.Select(c => c.Timestamp).Should().BeInAscendingOrder("rows seeded newest-first come back oldest-first")
            .And.Equal(
            Start.AddHours(-1), Start, Start.AddMinutes(5), Start.AddMinutes(10), Start.AddMinutes(15), Start.AddHours(2));
        cycles.Select(c => c.Id).Should().OnlyHaveUniqueItems().And.NotContain(Guid.Empty, "a temp names its setter by id");

        var capped = cycles[1];
        capped.AidAlgorithm.Should().Be(AidAlgorithm.AndroidAps);
        capped.Enacted.Should().BeTrue();
        capped.EnactedRate.Should().Be(2.4);
        capped.Iob.Should().Be(1.5);
        capped.SensitivityRatio.Should().Be(1.1);
        capped.HasReason.Should().BeTrue();
        capped.SuggestedChange.Should().BeTrue();
        capped.LimitReason.Should().Be(CappedReason);

        var held = cycles[2];
        held.HasReason.Should().BeTrue();
        held.SuggestedChange.Should().BeFalse("it asked for neither a temp nor a microbolus");
        held.LimitReason.Should().BeNull("its reason names no limit");

        cycles[3].HasReason.Should().BeFalse("an empty reason says nothing");

        var loop = cycles[4];
        loop.AidAlgorithm.Should().Be(AidAlgorithm.Loop);
        loop.HasReason.Should().BeFalse();
        loop.SuggestedChange.Should().BeNull();
    }

    [Fact]
    public async Task Reads_no_blob_column_and_the_suggestion_only_for_its_reason_and_its_keys()
    {
        var tenant = Guid.NewGuid();
        using (var scope = await fixture.BeginTenantScopeAsync(tenant))
            await SeedAsync(scope.ServiceProvider.GetRequiredService<IApsSnapshotRepository>());

        await using var conn = new NpgsqlConnection(fixture.AppConnectionString);
        await conn.OpenAsync();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT set_config('app.current_tenant_id', @tid, false), set_config('app.is_share', 'false', false)";
            cmd.Parameters.AddWithValue("tid", tenant.ToString());
            await cmd.ExecuteNonQueryAsync();
        }
        var statements = new StatementRecorder();
        await using var ctx = new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(conn)
            .AddInterceptors(statements)
            .Options) { TenantId = tenant };
        var repo = new ApsSnapshotRepository(
            new TestNocturneDbContextFactory(ctx), new SystemAuditContext(), NullLogger<ApsSnapshotRepository>.Instance);

        (await repo.GetLoopCyclesAsync(Start, Start.AddMinutes(20))).Should().HaveCount(6);

        var select = statements.All.Should().ContainSingle(s => s.StartsWith("SELECT"),
            "the cycles in the window and one beyond each end come in one round trip").Subject;
        select.Should().Contain("UNION ALL");
        foreach (var blob in new[]
                 {
                     "enacted_json", "predicted_default_json", "predicted_iob_json", "predicted_zt_json",
                     "predicted_cob_json", "predicted_uam_json", "loop_json", "additional_properties",
                 })
            select.Should().NotContain(blob);
        System.Text.RegularExpressions.Regex.Matches(select, @"jsonb_extract_path_text\([a-z0-9]+\.suggested_json, 'reason'\)")
            .Count.Should().Be(3, "each part of the union reads the reason once per row: {0}", select);
        System.Text.RegularExpressions.Regex.Matches(select, @"\?\|").Count.Should().Be(3, "and tests the keys once: {0}", select);
    }

    private static async Task SeedAsync(IApsSnapshotRepository repo)
    {
        var curve = JsonSerializer.Serialize(Enumerable.Range(0, 48).Select(i => 100 + i).ToArray());
        await repo.BulkCreateAsync(
        [
            new ApsSnapshot { Timestamp = Start.AddHours(3), AidAlgorithm = AidAlgorithm.Loop, DataSource = "loop-cycle-test" },
            new ApsSnapshot { Timestamp = Start.AddHours(2), AidAlgorithm = AidAlgorithm.Loop, DataSource = "loop-cycle-test" },
            new ApsSnapshot
            {
                Timestamp = Start.AddMinutes(15), AidAlgorithm = AidAlgorithm.Loop, Enacted = true, EnactedRate = 0,
                EnactedBolusVolume = 0.3, DataSource = "loop-cycle-test", LoopJson = "{\"enacted\":{\"received\":true}}",
                PredictedDefaultJson = curve,
            },
            new ApsSnapshot
            {
                Timestamp = Start.AddMinutes(10), AidAlgorithm = AidAlgorithm.AndroidAps, Enacted = true,
                DataSource = "loop-cycle-test",
                SuggestedJson = "{\"reason\":\"\",\"duration\":30}",
            },
            new ApsSnapshot
            {
                Timestamp = Start.AddMinutes(5), AidAlgorithm = AidAlgorithm.AndroidAps, Enacted = false,
                DataSource = "loop-cycle-test",
                SuggestedJson = "{\"reason\":\"Eventual BG 110 > 100, temp 2.4 >~ req 2.3U/hr. \",\"bg\":110}",
            },
            new ApsSnapshot
            {
                Timestamp = Start, AidAlgorithm = AidAlgorithm.AndroidAps, Enacted = true, EnactedRate = 2.4,
                EnactedDuration = 30, Iob = 1.5, SensitivityRatio = 1.1, DataSource = "loop-cycle-test",
                SuggestedJson = JsonSerializer.Serialize(new
                {
                    reason = CappedReason, rate = 2.4, duration = 30, units = 0.4,
                    predBGs = new { IOB = Enumerable.Range(0, 48).ToArray() },
                }),
                EnactedJson = "{\"received\":true,\"rate\":2.4}",
                PredictedIobJson = curve, PredictedZtJson = curve, PredictedCobJson = curve, PredictedUamJson = curve,
            },
            new ApsSnapshot { Timestamp = Start.AddHours(-1), AidAlgorithm = AidAlgorithm.AndroidAps, DataSource = "loop-cycle-test" },
            new ApsSnapshot { Timestamp = Start.AddHours(-2), AidAlgorithm = AidAlgorithm.AndroidAps, DataSource = "loop-cycle-test" },
        ], WriteOrigin.Backfill);
    }
}

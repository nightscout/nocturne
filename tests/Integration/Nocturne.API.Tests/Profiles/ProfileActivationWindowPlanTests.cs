using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.API.Tests.Integration.Profiles;

/// <summary>
/// The legacy treatment window read of profile activations walks
/// <c>ix_profile_activations_tenant_start_timestamp</c> whether or not it takes soft-deleted rows,
/// which the index holds as well as live ones. The query the read sends is captured and explained as
/// <c>nocturne_app</c>. The read tenant is older than four others, so walking the instance-wide
/// start index would pass their rows first.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ProfileActivationWindowPlanTests(ProfileActivationWindowPlanFixture fixture)
    : IClassFixture<ProfileActivationWindowPlanFixture>
{
    private const int Limit = 100;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WindowRead_WalksTheTenantStartIndex(bool includeDeleted)
    {
        await using var conn = new NpgsqlConnection(fixture.Database.AppConnectionString);
        await conn.OpenAsync();
        await QueryPlans.SetTenantAsync(conn, fixture.Tenant);

        var capture = new QueryPlans.LastReaderCapture();
        await using var ctx = new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(conn)
            .AddInterceptors(capture)
            .Options) { TenantId = fixture.Tenant };

        var rows = await LegacyTreatmentTables.ProfileActivationTable.InRangeAsync(
            Repositories(ctx), ctx, new LegacyTreatmentRange(null, null, Limit, Descending: true, includeDeleted), CancellationToken.None);
        rows.Should().HaveCount(Limit);
        rows.Any(r => r.Deleted).Should().Be(includeDeleted);

        var plan = await QueryPlans.ExplainAsync(conn, capture.Command!);
        plan.Descendants("Node Type").Should().NotContain("Seq Scan", plan.ToString());
        plan.Descendants("Index Name").Should().Equal(
            ["ix_profile_activations_tenant_start_timestamp"],
            $"the tenant's activations are read off the tenant-led index, not past every tenant's; plan: {plan}");
    }

    /// <summary>
    /// The real profile activation repository over <paramref name="ctx"/>. The other tables' repositories
    /// are strict, so a read that reaches one fails the test.
    /// </summary>
    private static LegacyTreatmentRepositories Repositories(NocturneDbContext ctx) => new(
        new Mock<IBolusRepository>(MockBehavior.Strict).Object,
        new Mock<ICarbIntakeRepository>(MockBehavior.Strict).Object,
        new Mock<IBGCheckRepository>(MockBehavior.Strict).Object,
        new Mock<INoteRepository>(MockBehavior.Strict).Object,
        new Mock<IDeviceEventRepository>(MockBehavior.Strict).Object,
        new Mock<ITempBasalRepository>(MockBehavior.Strict).Object,
        new Mock<IBolusCalculationRepository>(MockBehavior.Strict).Object,
        new ProfileActivationRepository(
            new TestNocturneDbContextFactory(ctx), new Mock<IAuditContext>().Object,
            NullLogger<ProfileActivationRepository>.Instance));
}

/// <summary>
/// A tenant of 20,000 treatment profile activations, every 25th soft-deleted, a month older than
/// four other tenants of as many.
/// </summary>
public sealed class ProfileActivationWindowPlanFixture : IAsyncLifetime
{
    public TestDatabase Database { get; private set; } = null!;

    public Guid Tenant { get; } = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        Database = await SharedPostgres.CreateMigratedDatabaseAsync("profile_activation_plan");
        await using var conn = new NpgsqlConnection(Database.SuperuserConnectionString);
        await conn.OpenAsync();

        foreach (var (tenant, age) in Enumerable.Range(0, 4).Select(_ => (Guid.NewGuid(), "0 days")).Append((Tenant, "30 days")))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES (@tid, 'plan-' || replace(@tid::text, '-', ''), 'plan-test', true, now(), now());
                INSERT INTO profile_activations
                    (id, tenant_id, start_timestamp, phase, origin, base_profile_name, percentage, timeshift_ms,
                     snapshot_is_customized, legacy_id, deleted_at, sys_created_at, sys_updated_at)
                SELECT gen_random_uuid(), @tid, now() - interval '{age}' - make_interval(mins => g), 'Active', 'User',
                       'Default', 100, 0, false, 'l-' || g, CASE WHEN g % 25 = 0 THEN now() END, now(), now()
                FROM generate_series(1, 20000) g;
                """;
            cmd.Parameters.AddWithValue("tid", tenant);
            await cmd.ExecuteNonQueryAsync();
        }

        await using var analyze = new NpgsqlCommand("ANALYZE profile_activations", conn);
        await analyze.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;
}

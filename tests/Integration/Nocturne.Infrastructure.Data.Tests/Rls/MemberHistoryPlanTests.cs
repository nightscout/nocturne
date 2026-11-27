using Nocturne.Core.Contracts.Auth;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// The member history's page reads <c>ix_auth_audit_log_tenant_member_changes</c>, and a page narrowed to
/// one member <c>ix_auth_audit_log_tenant_target_member_changes</c>; the count reads either, since both hold
/// the tenant's member changes. None reads the tenant's or the member's sign-in rows. The log holds the
/// sign-ins of one tenant, a member of which signs in often, that tenant's member changes, and the rows of
/// many other tenants, so the planner reads the tenant's share as small. The commands
/// <see cref="MemberHistoryReader"/> sends are captured and explained as <c>nocturne_app</c>, both with the
/// values of their parameters and as the generic plan the runtime pool's auto-prepare settles on.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class MemberHistoryPlanTests(RlsCompletenessFixture fx)
{
    private const int SignIns = 20_000;
    private const int MemberChanges = 300;
    private const int OtherTenants = 300;
    private const int Limit = 25;
    private const string MemberChangeIndex = "ix_auth_audit_log_tenant_member_changes";
    private const string TargetMemberChangeIndex = "ix_auth_audit_log_tenant_target_member_changes";

    private static readonly string[] WalksOfTheWholeLog =
    [
        "ix_auth_audit_log_tenant_created",
        "ix_auth_audit_log_event_type",
        "ix_auth_audit_log_target_subject_created",
        "ix_auth_audit_log_target_subject_id",
    ];

    [Fact]
    public async Task TheCountAndThePageReadTheMemberChangeIndex()
    {
        var tenant = Guid.NewGuid();
        var member = Guid.NewGuid();
        await SeedAsync(tenant, member);

        await using var conn = await fx.OpenAppConnectionAsync();
        await conn.PinTenantAsync(tenant);
        var capture = new QueryPlans.LastReaderCapture();
        await using var ctx = new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(conn)
            .AddInterceptors(capture)
            .Options) { TenantId = tenant };
        var reader = new MemberHistoryReader(new TestNocturneDbContextFactory(ctx));

        foreach (var (read, narrowedTo, offset, total, index) in new (string, Guid?, int, int, string)[]
        {
            ("the first page", null, 0, MemberChanges, MemberChangeIndex),
            ("a deep page", null, 100, MemberChanges, MemberChangeIndex),
            ("a page narrowed to one member", member, 0, MemberChanges / 5, TargetMemberChangeIndex),
        })
        {
            capture.Commands.Clear();
            var page = await reader.GetAsync(tenant, narrowedTo, Limit, offset, DateTime.UtcNow);
            page.Pagination.Total.Should().Be(total, read);
            page.Data.Should().HaveCount(Limit, read);

            capture.Commands.Should().HaveCountGreaterThanOrEqualTo(2, "the read sends the count, then the page");
            foreach (var (name, command) in new[] { ("count", capture.Commands[0]), ("page", capture.Commands[1]) })
            {
                foreach (var generic in new[] { false, true })
                {
                    var plan = generic
                        ? await QueryPlans.ExplainGenericAsync(conn, command)
                        : await QueryPlans.ExplainAsync(conn, command);
                    var because = $"{read}, {name}, {(generic ? "generic" : "custom")} plan: {plan}";
                    plan.Descendants("Node Type").Should().NotContain("Seq Scan", because);
                    var indexes = plan.Descendants("Index Name").ToList();
                    indexes.Should().IntersectWith(
                        narrowedTo is null && name == "count" ? [MemberChangeIndex, TargetMemberChangeIndex] : [index], because);
                    indexes.Should().NotIntersectWith(WalksOfTheWholeLog, because);
                }
            }
        }
    }

    private async Task SeedAsync(Guid tenant, Guid member)
    {
        await using var conn = await fx.OpenMigratorConnectionAsync();
        await conn.SeedTenantAsync(tenant, $"plan-{tenant:N}", "plan-test");

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $$"""
            INSERT INTO auth_audit_log (id, event_type, tenant_id, target_subject_id, created_at, success, details)
            SELECT gen_random_uuid(), (@signInTypes)[1 + g % cardinality(@signInTypes)], @tid,
                   CASE WHEN g % 2 = 0 THEN @member ELSE gen_random_uuid() END,
                   now() - make_interval(mins => g), true, NULL
            FROM generate_series(1, {{SignIns}}) g;

            INSERT INTO auth_audit_log (id, event_type, tenant_id, target_subject_id, created_at, success, details)
            SELECT gen_random_uuid(), (@changeTypes)[1 + g % cardinality(@changeTypes)], @tid,
                   CASE WHEN g % 5 = 0 THEN @member ELSE gen_random_uuid() END,
                   now() - make_interval(mins => g * 150), true,
                   jsonb_build_object('{{AuthAuditKind.Key}}', '{{AuthAuditKind.Tenant}}')
            FROM generate_series(1, {{MemberChanges}}) g;

            INSERT INTO auth_audit_log (id, event_type, tenant_id, target_subject_id, created_at, success, details)
            SELECT gen_random_uuid(), @signInTypes[1], t.id, gen_random_uuid(), now() - make_interval(mins => g), true, NULL
            FROM (SELECT gen_random_uuid() AS id FROM generate_series(1, {{OtherTenants}})) t, generate_series(1, 1000) g;

            INSERT INTO auth_audit_log (id, event_type, tenant_id, target_subject_id, created_at, success, details)
            SELECT gen_random_uuid(), (@changeTypes)[1 + g % cardinality(@changeTypes)], t.id, gen_random_uuid(),
                   now() - make_interval(mins => g * 150), true,
                   jsonb_build_object('{{AuthAuditKind.Key}}', '{{AuthAuditKind.Tenant}}')
            FROM (SELECT gen_random_uuid() AS id FROM generate_series(1, {{OtherTenants}})) t, generate_series(1, 100) g;

            ANALYZE auth_audit_log;
            """;
        cmd.Parameters.AddWithValue("tid", tenant);
        cmd.Parameters.AddWithValue("member", member);
        cmd.Parameters.AddWithValue("signInTypes", new[]
        {
            AuthAuditEventType.Login, AuthAuditEventType.TokenIssued, AuthAuditEventType.TokenRefreshed, AuthAuditEventType.Logout,
        });
        cmd.Parameters.AddWithValue("changeTypes", AuthAuditEventType.MemberChanges.ToArray());
        await cmd.ExecuteNonQueryAsync();
    }
}

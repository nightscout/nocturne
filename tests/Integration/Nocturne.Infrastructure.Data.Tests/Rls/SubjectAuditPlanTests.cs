using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.Infrastructure.Data.Tests.Rls;

/// <summary>
/// The statements <see cref="SubjectRepository.DeleteAsync"/> sends to scrub <c>auth_audit_log</c> of a
/// subject (the rows it acted on, and the rows it is party to) plan on the composites that lead with
/// <c>actor_subject_id</c> and <c>target_subject_id</c>, with no index of the target alone. The log holds
/// the sign-ins of many subjects, the rows of one subject who signs in often, and the rows of other
/// tenants, so a plan that cannot use an index reads the whole log. The real delete runs as
/// <c>nocturne_app</c>, its two <c>UPDATE</c> statements are captured, and each is explained without being
/// run, with the values of its parameters and as the generic plan the runtime pool's auto-prepare settles on.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class SubjectAuditPlanTests(RlsCompletenessFixture fx)
{
    private const int Rows = 30_000;
    private const int SubjectRows = 400;
    private const string TargetIndex = "ix_auth_audit_log_target_subject_created";
    private const string ActorIndex = "ix_auth_audit_log_actor_subject_created";

    [Fact]
    public async Task TheSubjectsAuditScrubPlansOnTheCompositeIndexes()
    {
        var tenant = Guid.NewGuid();
        var subject = Guid.NewGuid();
        await SeedAsync(tenant, subject);

        await using var conn = await fx.OpenAppConnectionAsync();
        var capture = new QueryPlans.NonQueryCapture();
        await using var ctx = new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(conn)
            .AddInterceptors(capture)
            .Options) { TenantId = tenant };

        var outcome = await SubjectDeletions.DeleteAsync(
            new TestNocturneDbContextFactory(ctx), AccountLinkRoutes.Off, subject, CancellationToken.None);
        outcome.Should().BeOfType<SubjectChange.Changed>();

        var updates = capture.Commands.Where(c => c.CommandText.TrimStart().StartsWith("UPDATE auth_audit_log")).ToList();
        updates.Should().HaveCount(2, "one statement per column set: the actor's address, then the details of every row it is party to");
        foreach (var (read, command, expected) in new[]
        {
            ("as actor", updates[0], new[] { ActorIndex }),
            ("as target or actor", updates[1], new[] { TargetIndex, ActorIndex }),
        })
        {
            foreach (var generic in new[] { false, true })
            {
                var plan = generic
                    ? await QueryPlans.ExplainGenericAsync(conn, command)
                    : await QueryPlans.ExplainAsync(conn, command);
                var because = $"{read}, {(generic ? "generic" : "custom")} plan: {plan}";
                plan.Descendants("Node Type").Should().NotContain("Seq Scan", because);
                plan.Descendants("Index Name").Distinct().Should().BeEquivalentTo(expected, because);
            }
        }
    }

    private async Task SeedAsync(Guid tenant, Guid subject)
    {
        await using var conn = await fx.OpenMigratorConnectionAsync();
        await conn.SeedTenantAsync(tenant, $"plan-{tenant:N}", "plan-test");
        await conn.ExecuteAsync("""
            INSERT INTO subjects (id, name, is_active, is_system_subject, created_at, updated_at, approval_status)
            VALUES (@id, 'plan-subject', true, false, now(), now(), 'Approved')
            """, ("id", subject));

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $$"""
            INSERT INTO auth_audit_log (id, event_type, tenant_id, target_subject_id, actor_subject_id, created_at, success, details)
            SELECT gen_random_uuid(), @login, @tid, s, s, now() - make_interval(mins => g), true, NULL
            FROM (SELECT g, gen_random_uuid() AS s FROM generate_series(1, {{Rows}}) g) r;

            INSERT INTO auth_audit_log (id, event_type, tenant_id, target_subject_id, actor_subject_id, created_at, success, details)
            SELECT gen_random_uuid(), @login, @tid, @subject, @subject, now() - make_interval(mins => g * 7), true, NULL
            FROM generate_series(1, {{SubjectRows}}) g;

            ANALYZE auth_audit_log;
            """;
        cmd.Parameters.AddWithValue("tid", tenant);
        cmd.Parameters.AddWithValue("subject", subject);
        cmd.Parameters.AddWithValue("login", AuthAuditEventType.Login);
        await cmd.ExecuteNonQueryAsync();
    }
}

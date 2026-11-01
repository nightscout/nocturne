using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Infrastructure.Data.Tests.Rls;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests;

/// <summary>
/// <see cref="TenantMembershipRepository.DecideRequestAsync"/> against real Postgres, under the
/// runtime role and Row Level Security: two contexts stand in for two requests deciding one request.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RLS completeness")]
public class MembershipRequestDecisionRaceTests
{
    private static readonly DateTime Now = new(2026, 10, 31, 9, 0, 0, DateTimeKind.Utc);

    private readonly RlsCompletenessFixture _fx;

    public MembershipRequestDecisionRaceTests(RlsCompletenessFixture fx) => _fx = fx;

    [Fact]
    public async Task A_deny_made_while_an_approval_holds_the_request_is_refused_and_the_approval_stands()
    {
        var seed = await SeedAsync();
        await using var provider = BuildProvider();
        await using var approving = await ContextAsync(provider, seed.Tenant);
        await using var denying = await ContextAsync(provider, seed.Tenant);
        var approvingUnit = DbUnitOfWork.Own(approving);
        var members = new TenantMembershipRepository(approvingUnit);
        var locked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var approval = approvingUnit.CommitAsync(async token =>
        {
            (await members.AddMemberAsync(new NewTenantMember(seed.Tenant, seed.Subject, [seed.Role]), token)).Should().BeTrue();
            (await members.DecideRequestAsync(seed.Tenant, seed.Request, Decision(MembershipRequestRecord.Approved, seed.Role), token)).Should().BeTrue();
            await approving.SaveChangesAsync(token);
            locked.SetResult();
            await release.Task;
        });
        await locked.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var deny = new TenantMembershipRepository(DbUnitOfWork.Own(denying))
            .DecideRequestAsync(seed.Tenant, seed.Request, Decision(MembershipRequestRecord.Denied, null), CancellationToken.None);
        await PostgresLockWaits.UntilBlockedAsync(_fx.AppConnectionString, deny);

        release.SetResult();
        await approval.WaitAsync(TimeSpan.FromSeconds(30));

        (await deny.WaitAsync(TimeSpan.FromSeconds(30))).Should().BeFalse("the approval committed first, so the request is no longer pending");
        await using var read = await ContextAsync(provider, seed.Tenant);
        (await read.MembershipRequests.SingleAsync(r => r.Id == seed.Request)).Status.Should().Be(MembershipRequestRecord.Approved);
        (await read.TenantMembers.AnyAsync(m => m.SubjectId == seed.Subject)).Should().BeTrue();
    }

    [Fact]
    public async Task An_approval_made_while_a_deny_holds_the_request_adds_no_member_and_the_deny_stands()
    {
        var seed = await SeedAsync();
        await using var provider = BuildProvider();
        await using var denying = await ContextAsync(provider, seed.Tenant);
        await using var approving = await ContextAsync(provider, seed.Tenant);
        var denyingUnit = DbUnitOfWork.Own(denying);
        var approvingUnit = DbUnitOfWork.Own(approving);
        var locked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var deny = denyingUnit.CommitAsync(async token =>
        {
            (await new TenantMembershipRepository(denyingUnit)
                .DecideRequestAsync(seed.Tenant, seed.Request, Decision(MembershipRequestRecord.Denied, null), token)).Should().BeTrue();
            locked.SetResult();
            await release.Task;
        });
        await locked.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var members = new TenantMembershipRepository(approvingUnit);
        var approval = approvingUnit.CommitAsync(async token =>
        {
            if (!await members.AddMemberAsync(new NewTenantMember(seed.Tenant, seed.Subject, [seed.Role]), token))
                return false;
            if (!await members.DecideRequestAsync(seed.Tenant, seed.Request, Decision(MembershipRequestRecord.Approved, seed.Role), token))
                return false;
            await approving.SaveChangesAsync(token);
            return true;
        });
        await PostgresLockWaits.UntilBlockedAsync(_fx.AppConnectionString, approval);

        release.SetResult();
        await deny.WaitAsync(TimeSpan.FromSeconds(30));

        (await approval.WaitAsync(TimeSpan.FromSeconds(30))).Should().BeFalse("the deny committed first, so the request is no longer pending");
        await using var read = await ContextAsync(provider, seed.Tenant);
        (await read.MembershipRequests.SingleAsync(r => r.Id == seed.Request)).Status.Should().Be(MembershipRequestRecord.Denied);
        (await read.TenantMembers.AnyAsync(m => m.SubjectId == seed.Subject)).Should().BeFalse();
    }

    private static MembershipRequestDecision Decision(string status, Guid? role) =>
        new(status, Guid.CreateVersion7(), Now, role is { } id ? [id] : null);

    private sealed record Seed(Guid Tenant, Guid Subject, Guid Role, Guid Request);

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddPostgreSqlInfrastructure(_fx.AppConnectionString, configuration: null);
        return services.BuildServiceProvider();
    }

    private static async Task<NocturneDbContext> ContextAsync(ServiceProvider provider, Guid tenantId)
    {
        var context = await provider.GetRequiredService<IDbContextFactory<NocturneDbContext>>().CreateDbContextAsync();
        context.TenantId = tenantId;
        return context;
    }

    private async Task<Seed> SeedAsync()
    {
        var tenant = Guid.NewGuid();
        await using (var conn = await _fx.OpenMigratorConnectionAsync())
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES (@id, @slug, 'decision-race-test', true, now(), now())
                """;
            cmd.Parameters.Add(new NpgsqlParameter("@id", tenant));
            cmd.Parameters.Add(new NpgsqlParameter("@slug", $"decide-{tenant:N}"));
            await cmd.ExecuteNonQueryAsync();
        }

        await using var provider = BuildProvider();
        await using var db = await ContextAsync(provider, tenant);
        var subject = new SubjectEntity { Id = Guid.CreateVersion7(), Name = "Asker" };
        var role = new TenantRoleEntity
        {
            Id = Guid.CreateVersion7(), TenantId = tenant, Name = "Carer", Slug = "carer", Permissions = ["glucose.read"],
        };
        var request = new MembershipRequestEntity
        {
            Id = Guid.CreateVersion7(), TenantId = tenant, SubjectId = subject.Id, Status = MembershipRequestRecord.Pending, CreatedAt = Now,
        };
        db.Subjects.Add(subject);
        db.TenantRoles.Add(role);
        db.MembershipRequests.Add(request);
        await db.SaveChangesAsync();
        return new Seed(tenant, subject.Id, role.Id, request.Id);
    }
}

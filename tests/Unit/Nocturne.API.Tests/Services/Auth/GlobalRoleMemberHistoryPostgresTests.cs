using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Audit;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;
using Nocturne.API.Tests.Services.Identity;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// A role given to or taken from a subject across every tenant, recorded by <see cref="SubjectService"/>
/// on a tenant's address, never reads as a change to that tenant's members, and a tenant member's own
/// role change always does, before and after deleting the subject (<see cref="SubjectDeletion"/>)
/// clears the details of both.
/// </summary>
[Trait("Category", "Integration")]
public class GlobalRoleMemberHistoryPostgresTests(GlobalRoleMemberHistoryPostgresTests.Database database)
    : IClassFixture<GlobalRoleMemberHistoryPostgresTests.Database>
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    public sealed class Database : IAsyncLifetime
    {
        public string AppConnectionString { get; private set; } = string.Empty;
        public string MigratorConnectionString { get; private set; } = string.Empty;

        public async Task InitializeAsync()
        {
            var database = await SharedPostgres.CreateMigratedDatabaseAsync("global_role_member_history");
            AppConnectionString = database.AppConnectionString;
            MigratorConnectionString = database.MigratorConnectionString;
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    private sealed record Scenario(
        Guid Tenant, Guid Person, string Role, SubjectService Service, SubjectDeletion Deletion, MemberHistoryReader History);

    private NocturneDbContext Context(Guid tenant) =>
        TestContexts.Context(database.AppConnectionString, tenant);

    private async Task<Scenario> SeedAsync(string slug)
    {
        var tenant = Guid.NewGuid();
        var role = $"{slug}-{tenant:N}";
        await using (var conn = new NpgsqlConnection(database.MigratorConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO tenants (id, slug, display_name, is_active, sys_created_at, sys_updated_at)
                VALUES (@id, @slug, 'global-role-history-test', true, now(), now())
                """;
            cmd.Parameters.Add(new NpgsqlParameter("@id", tenant));
            cmd.Parameters.Add(new NpgsqlParameter("@slug", role));
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var db = new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(database.MigratorConnectionString).Options))
        {
            db.Roles.Add(new RoleEntity { Id = Guid.CreateVersion7(), Name = role });
            await db.SaveChangesAsync();
        }

        var contexts = new SuppliedContextFactory(() => Context(tenant));
        var members = new TenantMembershipRepository(contexts, AccountLinkRoutes.Off);
        await members.SeedRolesAsync(tenant, Ct);
        var owner = (await members.FindRoleIdBySlugAsync(tenant, RoleSeeds.Owner, Ct))!.Value;
        var caretaker = (await members.FindRoleIdBySlugAsync(tenant, RoleSeeds.Caretaker, Ct))!.Value;
        var subjects = new SubjectRepository(contexts, AccountLinkRoutes.Off);
        var manager = await subjects.CreateAsync(new NewSubject($"{slug}-manager"), Ct);
        var person = await subjects.CreateAsync(new NewSubject($"{slug}-person"), Ct);
        (await members.AddMemberAsync(new NewTenantMember(tenant, manager, [owner]) { How = MembershipJoinHow.Direct, By = null }, Ct))
            .Should().BeTrue();
        (await members.AddMemberAsync(new NewTenantMember(tenant, person, [caretaker]) { How = MembershipJoinHow.Direct, By = manager }, Ct))
            .Should().BeTrue();

        var audit = new AuthAuditService(
            contexts, new HttpContextAccessor { HttpContext = new DefaultHttpContext() }, new AuditContext(),
            NullLogger<AuthAuditService>.Instance);
        var service = new SubjectService(
            subjects, audit, Mock.Of<IRecoveryCodeService>(), NullLogger<SubjectService>.Instance, TimeProvider.System);
        var deletion = new SubjectDeletion(
            contexts, subjects, members,
            new GrantRevocationService(new GuestSessionCacheService(new MemoryCache(new MemoryCacheOptions()))),
            audit, TimeProvider.System, InertTeamStaff.Announcer(), InertTeamStaff.Sites(), NullLogger<SubjectDeletion>.Instance);
        return new Scenario(tenant, person, role, service, deletion, new MemberHistoryReader(contexts));
    }

    private async Task<List<AuthAuditLogEntity>> RoleRowsAsync(Scenario s, Func<AuthAuditLogEntity, bool> where)
    {
        await using var db = Context(Guid.Empty);
        var rows = await db.AuthAuditLog.AsNoTracking()
            .Where(a => a.TargetSubjectId == s.Person
                && (a.EventType == AuthAuditEventType.RoleAssigned || a.EventType == AuthAuditEventType.RoleRemoved))
            .ToListAsync();
        return rows.Where(where).ToList();
    }

    private static bool Names(AuthAuditLogEntity row, string role) =>
        row.DetailsJson is not null && JsonDocument.Parse(row.DetailsJson).RootElement.GetProperty("role").GetString() == role;

    private async Task<List<Guid>> HistoryIdsAsync(Scenario s) =>
        (await s.History.GetAsync(s.Tenant, null, 100, 0, DateTime.UtcNow, Ct)).Data.Select(e => e.Id).ToList();

    private static async Task GiveAndTakeAsync(Scenario s)
    {
        (await s.Service.AssignRoleAsync(s.Person, s.Role)).Should().BeTrue();
        (await s.Service.RemoveRoleAsync(s.Person, s.Role)).Should().BeTrue();
    }

    [Fact]
    public async Task AnInstanceWideRoleChangeNeverAppearsInATenantsMemberHistory()
    {
        var s = await SeedAsync("grh-global");
        await GiveAndTakeAsync(s);
        var instanceWide = (await RoleRowsAsync(s, row => Names(row, s.Role)))
            .Select(r => r.Id).ToList();
        instanceWide.Should().HaveCount(2);

        (await HistoryIdsAsync(s)).Should().NotIntersectWith(instanceWide);

        (await s.Deletion.DeleteAsync(s.Person, evenLastOwner: false, Ct)).Change.Should().BeOfType<SubjectChange.Changed>();

        (await RoleRowsAsync(s, row => instanceWide.Contains(row.Id))).Should().OnlyContain(r => r.DetailsJson == null);
        (await HistoryIdsAsync(s)).Should().NotIntersectWith(instanceWide);
    }

    [Fact]
    public async Task AnInstanceWideRoleRowSaysGlobalAndCarriesNoTenant()
    {
        var s = await SeedAsync("grh-shape");
        await GiveAndTakeAsync(s);

        var rows = await RoleRowsAsync(s, row => Names(row, s.Role));

        rows.Select(r => r.EventType).Should().BeEquivalentTo(AuthAuditEventType.RoleAssigned, AuthAuditEventType.RoleRemoved);
        rows.Should().OnlyContain(r => r.TenantId == null);
        rows.Select(r => JsonDocument.Parse(r.DetailsJson!).RootElement.GetProperty(AuthAuditKind.Key).GetString())
            .Should().OnlyContain(kind => kind == AuthAuditKind.Global);
    }

    [Fact]
    public async Task ATenantMembersRoleChangeStillAppearsBeforeAndAfterTheSubjectIsDeleted()
    {
        var s = await SeedAsync("grh-member");
        await GiveAndTakeAsync(s);
        var member = (await RoleRowsAsync(s, row => !Names(row, s.Role))).Select(r => r.Id).ToList();
        member.Should().ContainSingle();

        (await HistoryIdsAsync(s)).Should().Contain(member);
        (await s.History.GetAsync(s.Tenant, s.Person, 100, 0, DateTime.UtcNow, Ct)).Data
            .Should().ContainSingle(e => e.Id == member[0]).Which.Kind.Should().Be(MemberHistoryKind.RoleGiven);

        (await s.Deletion.DeleteAsync(s.Person, evenLastOwner: false, Ct)).Change.Should().BeOfType<SubjectChange.Changed>();

        (await HistoryIdsAsync(s)).Should().Contain(member);
    }
}

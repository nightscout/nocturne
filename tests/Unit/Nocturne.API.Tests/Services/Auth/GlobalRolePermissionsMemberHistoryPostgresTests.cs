using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Identity;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// A global role's permission change made on a tenant's address and recorded by
/// <see cref="RoleRepository"/> is never part of that tenant's member history, including after deleting
/// the subject who made it clears the row's details.
/// </summary>
[Trait("Category", "Integration")]
public class GlobalRolePermissionsMemberHistoryPostgresTests(GlobalRolePermissionsMemberHistoryPostgresTests.Database database)
    : IClassFixture<GlobalRolePermissionsMemberHistoryPostgresTests.Database>
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTime Now = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);

    public sealed class Database() : MigratedDatabase("global_role_permissions_history");

    private NocturneDbContext Context(Guid tenant, Guid? actor = null) =>
        TestContexts.Context(database.AppConnectionString, tenant)
            .ActingAs(new TestAuditActor { SubjectId = actor });

    [Fact]
    public async Task AGlobalRolePermissionChangeNeverAppearsInATenantsMemberHistory()
    {
        var tenant = Guid.NewGuid();
        var roleName = $"grp-{tenant:N}";
        var roleId = Guid.CreateVersion7();
        await using (var conn = new NpgsqlConnection(database.MigratorConnectionString))
        {
            await conn.OpenAsync();
            await conn.SeedTenantAsync(tenant, roleName, "global-role-permissions-history-test");
        }

        await using (var db = new NocturneDbContext(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql(database.MigratorConnectionString).Options))
        {
            db.Roles.Add(new RoleEntity { Id = roleId, Name = roleName });
            await db.SaveChangesAsync();
        }

        var admin = await new SubjectRepository(new SuppliedContextFactory(() => Context(tenant)), AccountLinkRoutes.Off)
            .CreateAsync(new NewSubject($"grp-admin-{tenant:N}"), Ct);
        var contexts = new SuppliedContextFactory(() => Context(tenant, admin));

        (await new RoleRepository(contexts).UpdateAsync(roleId, roleName, null, ["glucose.read"], Ct)).Should().NotBeNull();

        var written = await PermissionRowsAsync(roleId);
        written.Should().ContainSingle().Which.TenantId.Should().BeNull();
        JsonDocument.Parse(written[0].DetailsJson!).RootElement.GetProperty(AuthAuditKind.Key).GetString()
            .Should().Be(AuthAuditKind.Global);

        var history = new MemberHistoryReader(contexts);
        (await history.GetAsync(tenant, null, 100, 0, Now, Ct)).Data.Should().BeEmpty();

        (await SubjectDeletions.DeleteAsync(new SuppliedContextFactory(() => Context(tenant)), AccountLinkRoutes.Off, admin, Ct)).Should().BeOfType<SubjectChange.Changed>();

        (await PermissionRowsAsync(roleId)).Should().ContainSingle().Which.DetailsJson.Should().BeNull();
        (await history.GetAsync(tenant, null, 100, 0, Now, Ct)).Data.Should().BeEmpty();
    }

    private async Task<List<AuthAuditLogEntity>> PermissionRowsAsync(Guid roleId)
    {
        await using var db = Context(Guid.Empty);
        var rows = await db.AuthAuditLog.AsNoTracking()
            .Where(a => a.EventType == AuthAuditEventType.RolePermissionsChanged)
            .ToListAsync();
        return rows.Where(a => a.DetailsJson is null || a.DetailsJson.Contains(roleId.ToString())).ToList();
    }
}

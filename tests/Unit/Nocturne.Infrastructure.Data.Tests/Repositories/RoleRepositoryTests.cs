using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary><see cref="RoleRepository"/> over SQLite: the global role catalogue and its subject assignments.</summary>
public class RoleRepositoryTests : IDisposable
{
    private static readonly DateTime Past = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly Guid _tenant = Guid.CreateVersion7();
    private readonly SqliteTestDatabase _db;

    public RoleRepositoryTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(_tenant);
    }

    public void Dispose() => _db.Dispose();

    private RoleRepository Roles() => new(new TestNocturneDbContextFactory(_db.CreateContext(Guid.Empty)));

    private RoleRepository RolesActingAs(IAuditContext actor, Guid? tenant = null) =>
        new(new TestNocturneDbContextFactory(_db.CreateContext(tenant ?? Guid.Empty).ActingAs(actor)));

    [Fact]
    public async Task ACreatedRoleReadsBackByIdAndNameInNameOrder()
    {
        var readable = await Roles().CreateAsync(new NewRole("readable", "reads", ["api:entries:read"], IsSystemRole: true), CancellationToken.None);
        var admin = await Roles().CreateAsync(new NewRole("admin", null, ["*"], IsSystemRole: true), CancellationToken.None);

        (await Roles().ListAsync(CancellationToken.None)).Select(r => r.Name).Should().Equal("admin", "readable");
        (await Roles().GetAsync(readable.Id, CancellationToken.None)).Should().BeEquivalentTo(new
        {
            Name = "readable", Description = "reads", Permissions = new List<string> { "api:entries:read" }, IsSystemRole = true,
        });
        (await Roles().FindByNameAsync("admin", CancellationToken.None))!.Id.Should().Be(admin.Id);
        (await Roles().FindByNameAsync("nobody", CancellationToken.None)).Should().BeNull();
        (await Roles().LastModifiedAsync(CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task AnUpdateReplacesTheRoleAndTheSaveStampsIt()
    {
        var role = await Roles().CreateAsync(new NewRole("careportal", null, ["careportal:*"], IsSystemRole: false), CancellationToken.None);
        await using (var seed = _db.CreateContext())
            await seed.Roles.Where(r => r.Id == role.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.UpdatedAt, Past));

        var updated = await Roles().UpdateAsync(role.Id, "care", "renamed", ["careportal:*", "api:treatments:*"], CancellationToken.None);

        updated.Should().BeEquivalentTo(new { Name = "care", Description = "renamed" });
        updated!.Permissions.Should().Equal("careportal:*", "api:treatments:*");
        updated.UpdatedAt.Should().BeAfter(Past);
        (await Roles().UpdateAsync(Guid.CreateVersion7(), "x", null, [], CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task AnUpdateThatChangesThePermissionsAuditsTheGainedAndLostAgainstTheCaller()
    {
        var caller = Guid.CreateVersion7();
        var role = await Roles().CreateAsync(new NewRole("careportal", null, ["careportal:*", "api:entries:read"], IsSystemRole: false), CancellationToken.None);

        await RolesActingAs(new TestAuditActor { SubjectId = caller })
            .UpdateAsync(role.Id, "careportal", null, ["api:entries:read", "api:treatments:*"], CancellationToken.None);

        await using var db = _db.CreateContext();
        var row = await db.AuthAuditLog.AsNoTracking().SingleAsync();
        row.EventType.Should().Be(AuthAuditEventType.RolePermissionsChanged);
        row.ActorSubjectId.Should().Be(caller);
        row.TargetSubjectId.Should().BeNull();
        row.TenantId.Should().BeNull();
        row.DetailsJson.Should().Be(
            $$"""{"role_id":"{{role.Id}}","role":"careportal","gained":["api:treatments:*"],"lost":["careportal:*"],"kind":"global"}""");
    }

    [Fact]
    public async Task AnUpdateMadeUnderATenantRecordsNoTenantKindGlobalAndNamesTheRoleByItsNewName()
    {
        var role = await Roles().CreateAsync(new NewRole("careportal", null, ["careportal:*"], IsSystemRole: false), CancellationToken.None);

        await RolesActingAs(new TestAuditActor { SubjectId = Guid.CreateVersion7() }, _tenant)
            .UpdateAsync(role.Id, "care", null, ["api:entries:read"], CancellationToken.None);

        await using var db = _db.CreateContext();
        var row = await db.AuthAuditLog.AsNoTracking().SingleAsync();
        row.TenantId.Should().BeNull();
        row.DetailsJson.Should().Contain("\"role\":\"care\"").And.Contain($"\"kind\":\"{AuthAuditKind.Global}\"");
    }

    [Fact]
    public async Task AnUpdateThatLeavesThePermissionsAsTheyWereAuditsNothing()
    {
        var role = await Roles().CreateAsync(new NewRole("careportal", null, ["careportal:*", "api:entries:read"], IsSystemRole: false), CancellationToken.None);

        await RolesActingAs(new TestAuditActor { SubjectId = Guid.CreateVersion7() })
            .UpdateAsync(role.Id, "care", "renamed", ["api:entries:read", "careportal:*"], CancellationToken.None);

        await using var db = _db.CreateContext();
        (await db.AuthAuditLog.AsNoTracking().ToListAsync()).Should().BeEmpty();
        (await db.Roles.AsNoTracking().SingleAsync()).Name.Should().Be("care");
    }

    [Fact]
    public async Task ADeleteTakesTheSubjectAssignmentsWithItInOneSave()
    {
        var role = await Roles().CreateAsync(new NewRole("custom", null, ["a"], IsSystemRole: false), CancellationToken.None);
        var subjects = new SubjectRepository(new TestNocturneDbContextFactory(_db.CreateContext(Guid.Empty)), AccountLinkRoutes.Off);
        var one = await subjects.CreateAsync(new NewSubject("one", RoleNames: ["custom"]), CancellationToken.None);
        var two = await subjects.CreateAsync(new NewSubject("two", RoleNames: ["custom"]), CancellationToken.None);

        (await Roles().DeleteAsync(role.Id, CancellationToken.None)).Should().Be(2);

        (await Roles().GetAsync(role.Id, CancellationToken.None)).Should().BeNull();
        await using var check = _db.CreateContext();
        (await check.SubjectRoles.CountAsync(sr => sr.SubjectId == one || sr.SubjectId == two)).Should().Be(0);
        (await Roles().DeleteAsync(role.Id, CancellationToken.None)).Should().BeNull("a second delete finds nothing");
    }
}

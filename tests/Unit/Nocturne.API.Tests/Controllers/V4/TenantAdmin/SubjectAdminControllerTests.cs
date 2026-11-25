using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Controllers.V4.TenantAdmin;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.TenantAdmin;

[Trait("Category", "Unit")]
public class SubjectAdminControllerTests : IDisposable
{
    private readonly SqliteTestDatabase _db = TestDbContextFactory.CreateSqliteWithTenant(Guid.CreateVersion7());

    public void Dispose() => _db.Dispose();

    private SubjectRepository Subjects() =>
        new(new TestNocturneDbContextFactory(_db.CreateContext()), AccountLinkRoutes.Off);

    private SubjectAdminController Controller() =>
        new(Subjects()) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

    private async Task<Guid> AdminAsync(string name, bool active, bool system = false)
    {
        var id = await Subjects().CreateAsync(new NewSubject(name, IsActive: active, IsSystemSubject: system), CancellationToken.None);
        await Subjects().GrantPlatformAdminAsync([id], CancellationToken.None);
        return id;
    }

    [Fact]
    public async Task The_last_active_admin_cannot_be_demoted_while_another_admin_is_inactive()
    {
        var last = await AdminAsync("Last", active: true);
        await AdminAsync("Deactivated", active: false);

        var result = await Controller().SetPlatformAdmin(last, new SetPlatformAdminRequest(false));

        result.Should().BeOfType<ConflictObjectResult>();
        (await Subjects().GetAsync(last, CancellationToken.None))!.IsPlatformAdmin.Should().BeTrue();
    }

    [Fact]
    public async Task An_inactive_admin_can_be_demoted_while_one_active_admin_remains()
    {
        var active = await AdminAsync("Active", active: true);
        var deactivated = await AdminAsync("Deactivated", active: false);

        var result = await Controller().SetPlatformAdmin(deactivated, new SetPlatformAdminRequest(false));

        result.Should().BeOfType<NoContentResult>();
        (await Subjects().GetAsync(deactivated, CancellationToken.None))!.IsPlatformAdmin.Should().BeFalse();
        (await Subjects().GetAsync(active, CancellationToken.None))!.IsPlatformAdmin.Should().BeTrue();
    }

    [Fact]
    public async Task An_active_admin_can_be_demoted_while_another_active_admin_remains()
    {
        var first = await AdminAsync("First", active: true);
        await AdminAsync("Second", active: true);

        var result = await Controller().SetPlatformAdmin(first, new SetPlatformAdminRequest(false));

        result.Should().BeOfType<NoContentResult>();
        (await Subjects().GetAsync(first, CancellationToken.None))!.IsPlatformAdmin.Should().BeFalse();
    }

    [Fact]
    public async Task A_system_subject_can_be_demoted_while_one_person_admin_remains()
    {
        var person = await AdminAsync("Person", active: true);
        var system = await AdminAsync("System", active: true, system: true);

        (await Controller().SetPlatformAdmin(system, new SetPlatformAdminRequest(false))).Should().BeOfType<NoContentResult>();
        (await Controller().SetPlatformAdmin(person, new SetPlatformAdminRequest(false))).Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task An_unknown_subject_is_not_found()
    {
        (await Controller().SetPlatformAdmin(Guid.CreateVersion7(), new SetPlatformAdminRequest(true))).Should().BeOfType<NotFoundResult>();
    }
}

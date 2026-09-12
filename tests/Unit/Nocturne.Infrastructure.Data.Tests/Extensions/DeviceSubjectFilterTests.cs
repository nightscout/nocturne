using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.Infrastructure.Data.Tests.Extensions;

/// <summary>
/// The holder of a tenant's API tokens decides what those tokens inherit, so it has to be a subject
/// that is nobody and it has to be one per tenant.
/// </summary>
[Trait("Category", "Unit")]
public class DeviceSubjectFilterTests : IDisposable
{
    private readonly Guid _tenant = Guid.CreateVersion7();
    private readonly Guid _otherTenant = Guid.CreateVersion7();
    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;

    public DeviceSubjectFilterTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(_tenant, "site").SeedTenant(_otherTenant, "other");
        _context = _db.CreateContext(_tenant);
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task The_holder_is_a_system_subject_who_is_nobody()
    {
        var subjectId = await _context.DeviceSubjectOf(_tenant);

        var subject = await _context.Subjects.SingleAsync(s => s.Id == subjectId);

        // Issuing to a person makes the token inherit what that person is, which on a self-hosted
        // instance means the owner's platform-admin standing over every tenant.
        subject.IsSystemSubject.Should().BeTrue();
        subject.IsPlatformAdmin.Should().BeFalse();
        subject.IsDemoSubject.Should().BeFalse();
    }

    [Fact]
    public async Task The_holder_does_not_read_as_locked_out()
    {
        await _context.DeviceSubjectOf(_tenant);

        // It holds a membership and can never hold a passkey, which is the shape that puts a tenant
        // into recovery mode for every request.
        (await _context.OrphanedSubjectsOf(_tenant).ToListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task The_holder_carries_a_membership_that_does_not_cap_its_tokens()
    {
        var subjectId = await _context.DeviceSubjectOf(_tenant);

        // MemberScopeMiddleware intersects a grant's scopes with its subject's membership, so a
        // narrower membership here would silently cap every token rather than the one that
        // deserved it. The bound on a device token is the token.
        var member = await _context.TenantMembers
            .SingleAsync(m => m.TenantId == _tenant && m.SubjectId == subjectId);

        member.DirectPermissions.Should().Equal(Scope.FullAccess);
    }

    [Fact]
    public async Task Asking_twice_resolves_the_same_holder()
    {
        var first = await _context.DeviceSubjectOf(_tenant);
        var second = await _context.DeviceSubjectOf(_tenant);

        second.Should().Be(first);
        _context.Subjects.Count(s => s.Name == DeviceSubjectFilter.DeviceSubjectName).Should().Be(1);
    }

    [Fact]
    public async Task Each_tenant_gets_its_own()
    {
        var mine = await _context.DeviceSubjectOf(_tenant);

        await using var otherContext = _db.CreateContext(_otherTenant);
        var theirs = await otherContext.DeviceSubjectOf(_otherTenant);

        // A shared holder would make one tenant's membership the ceiling for another's tokens.
        theirs.Should().NotBe(mine);
    }

    [Fact]
    public async Task Looking_for_a_holder_does_not_create_one()
    {
        var found = await _context.FindDeviceSubjectOf(_tenant);

        // Reading the token list must not write rows into a tenant that has never had a token.
        found.Should().BeNull();
        _context.Subjects.Should().BeEmpty();
        _context.TenantMembers.Should().BeEmpty();
    }

    [Fact]
    public async Task Looking_finds_the_holder_once_it_exists()
    {
        var created = await _context.DeviceSubjectOf(_tenant);

        (await _context.FindDeviceSubjectOf(_tenant)).Should().Be(created);
    }

    [Fact]
    public async Task Another_tenants_holder_is_not_found()
    {
        await using var otherContext = _db.CreateContext(_otherTenant);
        await otherContext.DeviceSubjectOf(_otherTenant);

        (await _context.FindDeviceSubjectOf(_tenant)).Should().BeNull();
    }
}

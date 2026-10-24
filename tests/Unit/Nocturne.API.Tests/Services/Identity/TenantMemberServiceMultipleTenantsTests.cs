using FluentAssertions;
using Nocturne.API.Services.Identity;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Identity;

[Trait("Category", "Unit")]
public sealed class TenantMemberServiceMultipleTenantsTests : IDisposable
{
    private readonly SqliteTestDatabase _db = TestDbContextFactory.CreateSqlite();
    private readonly NocturneDbContext _context;
    private readonly TenantMemberService _service;
    private readonly Guid _subjectId = Guid.CreateVersion7();

    public TenantMemberServiceMultipleTenantsTests()
    {
        _context = _db.CreateContext(Guid.CreateVersion7());
        _context.Subjects.Add(new SubjectEntity { Id = _subjectId, Name = "Sam", IsActive = true });
        _context.SaveChanges();
        _service = new TenantMemberService(_context.AsFactory());
    }

    private void Join(string slug, bool isActive = true, Guid? subjectId = null)
    {
        var tenantId = Guid.CreateVersion7();
        _context.Tenants.Add(new TenantEntity { Id = tenantId, Slug = slug, DisplayName = slug, IsActive = isActive });
        _context.TenantMembers.Add(new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(), TenantId = tenantId, SubjectId = subjectId ?? _subjectId,
        });
        _context.SaveChanges();
    }

    [Fact]
    public async Task OneTenant_IsNotMultiple()
    {
        Join("alpha");

        (await _service.HasMultipleActiveTenantsAsync(_subjectId)).Should().BeFalse();
    }

    [Fact]
    public async Task TwoActiveTenants_AreMultiple()
    {
        Join("alpha");
        Join("bravo");

        (await _service.HasMultipleActiveTenantsAsync(_subjectId)).Should().BeTrue();
    }

    [Fact]
    public async Task AnInactiveTenant_DoesNotCountTowardTheSwitcher()
    {
        Join("alpha");
        Join("bravo", isActive: false);

        (await _service.HasMultipleActiveTenantsAsync(_subjectId)).Should().BeFalse();
    }

    [Fact]
    public async Task AnotherSubjectsMemberships_AreNotCounted()
    {
        var other = Guid.CreateVersion7();
        _context.Subjects.Add(new SubjectEntity { Id = other, Name = "Other", IsActive = true });
        _context.SaveChanges();
        Join("alpha");
        Join("bravo", subjectId: other);
        Join("charlie", subjectId: other);

        (await _service.HasMultipleActiveTenantsAsync(_subjectId)).Should().BeFalse();
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
    }
}

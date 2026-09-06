using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Configuration;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Identity;

/// <summary>
/// The order of a subject's tenant list is contractual: the UI takes the first entry as the
/// tenant that person owns, so an unordered read would rename "My Data" between requests.
/// </summary>
[Trait("Category", "Unit")]
public sealed class TenantServiceGetTenantsForSubjectTests : IDisposable
{
    private readonly SqliteTestDatabase _db = TestDbContextFactory.CreateSqlite();
    private readonly Guid _subjectId = Guid.CreateVersion7();

    public TenantServiceGetTenantsForSubjectTests()
    {
        using var db = _db.CreateContext();
        db.Subjects.Add(new SubjectEntity { Id = _subjectId, Name = "Sam" });
        db.SaveChanges();
    }

    private TenantService Service() => new(
        _db.ContextFactory,
        new MemoryCache(new MemoryCacheOptions()),
        Options.Create(new OperatorConfiguration()),
        Mock.Of<IHttpClientFactory>(),
        Mock.Of<ITenantRoleService>(),
        Mock.Of<ILogger<TenantService>>());

    private void SeedMembership(string slug, DateTime joinedAt)
    {
        var tenantId = Guid.CreateVersion7();
        using var db = _db.CreateContext(tenantId);

        db.Tenants.Add(new TenantEntity { Id = tenantId, Slug = slug, DisplayName = slug });
        var member = new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            SubjectId = _subjectId,
        };
        db.TenantMembers.Add(member);
        db.SaveChanges();

        // Inserting stamps SysCreatedAt with the current time, so a past join date can only be
        // written afterwards.
        member.SysCreatedAt = joinedAt;
        db.SaveChanges();
    }

    [Fact]
    public async Task GetTenantsForSubjectAsync_ordersByWhenTheSubjectJoined()
    {
        SeedMembership("alpha", new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        SeedMembership("zulu", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var tenants = await Service().GetTenantsForSubjectAsync(_subjectId);

        tenants.Select(t => t.Slug).Should().Equal("zulu", "alpha");
    }

    [Fact]
    public async Task GetTenantsForSubjectAsync_breaksATieOnSlug()
    {
        var joinedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        SeedMembership("zulu", joinedAt);
        SeedMembership("alpha", joinedAt);

        var tenants = await Service().GetTenantsForSubjectAsync(_subjectId);

        tenants.Select(t => t.Slug).Should().Equal("alpha", "zulu");
    }

    public void Dispose() => _db.Dispose();
}

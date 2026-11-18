using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Configuration;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Identity;

public class TenantSlugChangeTests : IDisposable
{
    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 14, 9, 0, 0, TimeSpan.Zero));
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly TenantService _service;

    public TenantSlugChangeTests()
    {
        _db = TestDbContextFactory.CreateSqlite();
        _context = _db.CreateContext();
        _context.Tenants.Add(new TenantEntity { Id = _tenantId, Slug = "sam", DisplayName = "Sam", IsActive = true });
        _context.SaveChanges();

        _service = new TenantService(
            _context.AsFactory(),
            new TenantRepository(_context.AsFactory()),
            Mock.Of<ITenantMembershipRepository>(),
            Mock.Of<ISubjectRepository>(),
            new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new OperatorConfiguration()),
            Mock.Of<IHttpClientFactory>(),
            Mock.Of<ITenantRoleService>(),
            grantRevocation: null!,
            _time,
            NullLogger<TenantService>.Instance);
    }

    public void Dispose() => _db.Dispose();

    private async Task<string> StoredSlugAsync() =>
        (await _context.Tenants.AsNoTracking().SingleAsync(t => t.Id == _tenantId)).Slug;

    [Fact]
    public async Task A_change_is_recorded_and_the_next_one_waits_an_hour()
    {
        (await _service.ChangeSlugAsync(_tenantId, "sam-taylor")).IsValid.Should().BeTrue();

        var settings = await _service.GetSettingsAsync(_tenantId);
        settings.Slug.Should().Be("sam-taylor");
        settings.NextSlugChangeAt.Should().Be(_time.GetUtcNow().UtcDateTime + ITenantService.SlugChangeInterval);

        _time.Advance(TimeSpan.FromMinutes(59));
        var refused = await _service.ChangeSlugAsync(_tenantId, "sam-t");

        refused.IsValid.Should().BeFalse();
        refused.Message.Should().Be("You can change the address one time each hour. Try again later.");
        (await StoredSlugAsync()).Should().Be("sam-taylor");
    }

    [Fact]
    public async Task After_an_hour_the_address_can_change_again()
    {
        await _service.ChangeSlugAsync(_tenantId, "sam-taylor");
        _time.Advance(ITenantService.SlugChangeInterval);

        (await _service.GetSettingsAsync(_tenantId)).NextSlugChangeAt.Should().BeNull();
        (await _service.ChangeSlugAsync(_tenantId, "sam-t")).IsValid.Should().BeTrue();
        (await StoredSlugAsync()).Should().Be("sam-t");
    }

    [Fact]
    public async Task A_tenant_that_never_changed_its_address_can_change_it_now()
    {
        (await _service.GetSettingsAsync(_tenantId)).NextSlugChangeAt.Should().BeNull();
    }

    [Fact]
    public async Task A_refused_change_does_not_start_the_wait()
    {
        _context.Tenants.Add(new TenantEntity { Id = Guid.CreateVersion7(), Slug = "taken", DisplayName = "Taken", IsActive = true });
        await _context.SaveChangesAsync();

        (await _service.ChangeSlugAsync(_tenantId, "taken")).IsValid.Should().BeFalse();

        (await _service.GetSettingsAsync(_tenantId)).NextSlugChangeAt.Should().BeNull();
        (await _service.ChangeSlugAsync(_tenantId, "sam-taylor")).IsValid.Should().BeTrue();
    }
}

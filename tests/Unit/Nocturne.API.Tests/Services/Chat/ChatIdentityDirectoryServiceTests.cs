using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Services.Chat;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Services.Chat;

[Trait("Category", "Unit")]
public class ChatIdentityDirectoryServiceTests : IDisposable
{
    private const string Platform = "discord";
    private const string UserA = "discord-user-a";
    private const string UserB = "discord-user-b";

    private readonly DbConnection _connection;
    private readonly DbContextOptions<NocturneDbContext> _options;
    private readonly TestDbContextFactory _factory;
    private readonly ChatIdentityDirectoryService _service;

    public ChatIdentityDirectoryServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .Options;

        using (var db = new NocturneDbContext(_options))
        {
            db.Database.EnsureCreated();
        }

        _factory = new TestDbContextFactory(_options);
        _service = new ChatIdentityDirectoryService(
            _factory,
            Mock.Of<ILogger<ChatIdentityDirectoryService>>());
    }

    public void Dispose() => _connection.Dispose();

    private sealed class TestDbContextFactory(DbContextOptions<NocturneDbContext> options)
        : IDbContextFactory<NocturneDbContext>
    {
        public NocturneDbContext CreateDbContext() => new(options);
        public Task<NocturneDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }

    // ---- GetCandidatesAsync ----

    [Fact]
    public async Task GetCandidatesAsync_returns_empty_when_no_links()
    {
        var result = await _service.GetCandidatesAsync(Platform, UserA, default);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetCandidatesAsync_returns_single_link_when_one_exists()
    {
        await _service.CreateLinkAsync(Platform, UserA, Guid.CreateVersion7(), Guid.CreateVersion7(), "lily", "Lily", default);
        var result = await _service.GetCandidatesAsync(Platform, UserA, default);
        result.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetCandidatesAsync_returns_all_links_when_multiple_exist()
    {
        await _service.CreateLinkAsync(Platform, UserA, Guid.CreateVersion7(), Guid.CreateVersion7(), "lily", "Lily", default);
        await _service.CreateLinkAsync(Platform, UserA, Guid.CreateVersion7(), Guid.CreateVersion7(), "oliver", "Oliver", default);
        var result = await _service.GetCandidatesAsync(Platform, UserA, default);
        result.Should().HaveCount(2);
    }

    // ---- CreateLinkAsync ----

    [Fact]
    public async Task CreateLinkAsync_marks_first_link_as_default()
    {
        var link = await _service.CreateLinkAsync(Platform, UserA, Guid.CreateVersion7(), Guid.CreateVersion7(), "lily", "Lily", default);
        link.IsDefault.Should().BeTrue();
    }

    [Fact]
    public async Task CreateLinkAsync_marks_subsequent_link_as_non_default()
    {
        await _service.CreateLinkAsync(Platform, UserA, Guid.CreateVersion7(), Guid.CreateVersion7(), "lily", "Lily", default);
        var second = await _service.CreateLinkAsync(Platform, UserA, Guid.CreateVersion7(), Guid.CreateVersion7(), "oliver", "Oliver", default);
        second.IsDefault.Should().BeFalse();
    }

    [Fact]
    public async Task CreateLinkAsync_is_idempotent_on_same_platform_user_tenant()
    {
        var tenantId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        var first = await _service.CreateLinkAsync(Platform, UserA, tenantId, userId, "lily", "Lily", default);
        var second = await _service.CreateLinkAsync(Platform, UserA, tenantId, userId, "different", "Different", default);
        second.Id.Should().Be(first.Id);
        second.Label.Should().Be("lily");
        second.DisplayName.Should().Be("Lily");
    }

    [Fact]
    public async Task CreateLinkAsync_auto_suffixes_label_collision()
    {
        await _service.CreateLinkAsync(Platform, UserA, Guid.CreateVersion7(), Guid.CreateVersion7(), "lily", "Lily 1", default);
        var second = await _service.CreateLinkAsync(Platform, UserA, Guid.CreateVersion7(), Guid.CreateVersion7(), "lily", "Lily 2", default);
        second.Label.Should().Be("lily-2");
    }

    [Fact]
    public async Task CreateLinkAsync_auto_suffixes_multiple_collisions()
    {
        await _service.CreateLinkAsync(Platform, UserA, Guid.CreateVersion7(), Guid.CreateVersion7(), "lily", "Lily 1", default);
        var b = await _service.CreateLinkAsync(Platform, UserA, Guid.CreateVersion7(), Guid.CreateVersion7(), "lily", "Lily 2", default);
        var c = await _service.CreateLinkAsync(Platform, UserA, Guid.CreateVersion7(), Guid.CreateVersion7(), "lily", "Lily 3", default);
        b.Label.Should().Be("lily-2");
        c.Label.Should().Be("lily-3");
    }

    // ---- SetDefaultAsync ----

    [Fact]
    public async Task SetDefaultAsync_promotes_target_and_clears_other_defaults()
    {
        var a = await _service.CreateLinkAsync(Platform, UserA, Guid.CreateVersion7(), Guid.CreateVersion7(), "lily", "Lily", default);
        var b = await _service.CreateLinkAsync(Platform, UserA, Guid.CreateVersion7(), Guid.CreateVersion7(), "oliver", "Oliver", default);
        a.IsDefault.Should().BeTrue();
        b.IsDefault.Should().BeFalse();

        await _service.SetDefaultAsync(b.Id, tenantScope: null, ct: default);

        var aAfter = await _service.GetByIdAsync(a.Id, tenantScope: null, ct: default);
        var bAfter = await _service.GetByIdAsync(b.Id, tenantScope: null, ct: default);
        aAfter!.IsDefault.Should().BeFalse();
        bAfter!.IsDefault.Should().BeTrue();
    }

    [Fact]
    public async Task SetDefaultAsync_throws_when_link_not_found()
    {
        var act = async () => await _service.SetDefaultAsync(Guid.CreateVersion7(), tenantScope: null, ct: default);
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    // ---- RenameLabelAsync ----

    [Fact]
    public async Task RenameLabelAsync_updates_label()
    {
        var a = await _service.CreateLinkAsync(Platform, UserA, Guid.CreateVersion7(), Guid.CreateVersion7(), "lily", "Lily", default);
        await _service.RenameLabelAsync(a.Id, tenantScope: null, "rose", ct: default);
        var after = await _service.GetByIdAsync(a.Id, tenantScope: null, ct: default);
        after!.Label.Should().Be("rose");
    }

    [Fact]
    public async Task RenameLabelAsync_throws_on_collision()
    {
        await _service.CreateLinkAsync(Platform, UserA, Guid.CreateVersion7(), Guid.CreateVersion7(), "lily", "Lily", default);
        var b = await _service.CreateLinkAsync(Platform, UserA, Guid.CreateVersion7(), Guid.CreateVersion7(), "oliver", "Oliver", default);

        var act = async () => await _service.RenameLabelAsync(b.Id, tenantScope: null, "lily", ct: default);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ---- UpdateDisplayNameAsync ----

    [Fact]
    public async Task UpdateDisplayNameAsync_updates_display_name()
    {
        var a = await _service.CreateLinkAsync(Platform, UserA, Guid.CreateVersion7(), Guid.CreateVersion7(), "lily", "Lily", default);
        await _service.UpdateDisplayNameAsync(a.Id, tenantScope: null, "Lily Renamed", ct: default);
        var after = await _service.GetByIdAsync(a.Id, tenantScope: null, ct: default);
        after!.DisplayName.Should().Be("Lily Renamed");
    }

    // ---- RevokeAsync ----

    [Fact]
    public async Task RevokeAsync_hard_deletes_row()
    {
        var a = await _service.CreateLinkAsync(Platform, UserA, Guid.CreateVersion7(), Guid.CreateVersion7(), "lily", "Lily", default);
        await _service.RevokeAsync(a.Id, tenantScope: null, ct: default);
        var after = await _service.GetByIdAsync(a.Id, tenantScope: null, ct: default);
        after.Should().BeNull();
    }

    // ---- GetByTenantAsync ----

    [Fact]
    public async Task GetByTenantAsync_returns_only_links_for_that_tenant()
    {
        var t1 = Guid.CreateVersion7();
        var t2 = Guid.CreateVersion7();
        await _service.CreateLinkAsync(Platform, UserA, t1, Guid.CreateVersion7(), "lily", "Lily", default);
        await _service.CreateLinkAsync(Platform, UserB, t2, Guid.CreateVersion7(), "oliver", "Oliver", default);

        var result = await _service.GetByTenantAsync(t1, default);
        result.Should().HaveCount(1);
        result[0].TenantId.Should().Be(t1);
    }

    // ---- GetByPlatformAndUserAsync ----

    [Fact]
    public async Task GetByPlatformAndUserAsync_returns_exact_match_when_label_provided()
    {
        await _service.CreateLinkAsync(Platform, UserA, Guid.CreateVersion7(), Guid.CreateVersion7(), "lily", "Lily", default);
        await _service.CreateLinkAsync(Platform, UserA, Guid.CreateVersion7(), Guid.CreateVersion7(), "oliver", "Oliver", default);

        var result = await _service.GetByPlatformAndUserAsync(Platform, UserA, "oliver", default);
        result.Should().NotBeNull();
        result!.Label.Should().Be("oliver");
    }

    [Fact]
    public async Task GetByPlatformAndUserAsync_returns_default_when_label_null_and_multiple_exist()
    {
        var a = await _service.CreateLinkAsync(Platform, UserA, Guid.CreateVersion7(), Guid.CreateVersion7(), "lily", "Lily", default);
        await _service.CreateLinkAsync(Platform, UserA, Guid.CreateVersion7(), Guid.CreateVersion7(), "oliver", "Oliver", default);

        var result = await _service.GetByPlatformAndUserAsync(Platform, UserA, null, default);
        result.Should().NotBeNull();
        result!.Id.Should().Be(a.Id);
    }

    [Fact]
    public async Task GetByPlatformAndUserAsync_returns_single_when_only_one_exists()
    {
        var a = await _service.CreateLinkAsync(Platform, UserA, Guid.CreateVersion7(), Guid.CreateVersion7(), "lily", "Lily", default);
        // Force IsDefault=false to simulate "no default"
        using (var db = _factory.CreateDbContext())
        {
            var entity = await db.ChatIdentityDirectory.FirstAsync(d => d.Id == a.Id);
            entity.IsDefault = false;
            await db.SaveChangesAsync();
        }

        var result = await _service.GetByPlatformAndUserAsync(Platform, UserA, null, default);
        result.Should().NotBeNull();
        result!.Id.Should().Be(a.Id);
    }

    [Fact]
    public async Task GetByPlatformAndUserAsync_returns_null_when_label_null_and_ambiguous()
    {
        var a = await _service.CreateLinkAsync(Platform, UserA, Guid.CreateVersion7(), Guid.CreateVersion7(), "lily", "Lily", default);
        await _service.CreateLinkAsync(Platform, UserA, Guid.CreateVersion7(), Guid.CreateVersion7(), "oliver", "Oliver", default);
        // Clear default flag without setting another
        using (var db = _factory.CreateDbContext())
        {
            var entity = await db.ChatIdentityDirectory.FirstAsync(d => d.Id == a.Id);
            entity.IsDefault = false;
            await db.SaveChangesAsync();
        }

        var result = await _service.GetByPlatformAndUserAsync(Platform, UserA, null, default);
        result.Should().BeNull();
    }

    // ---- GetByIdAsync ----

    [Fact]
    public async Task GetByIdAsync_returns_link_or_null()
    {
        var a = await _service.CreateLinkAsync(Platform, UserA, Guid.CreateVersion7(), Guid.CreateVersion7(), "lily", "Lily", default);
        (await _service.GetByIdAsync(a.Id, tenantScope: null, ct: default)).Should().NotBeNull();
        (await _service.GetByIdAsync(Guid.CreateVersion7(), tenantScope: null, ct: default)).Should().BeNull();
    }
}

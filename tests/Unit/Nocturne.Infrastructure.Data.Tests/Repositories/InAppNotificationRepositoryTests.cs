using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

[Trait("Category", "Unit")]
[Trait("Category", "Repository")]
public class InAppNotificationRepositoryTests : IDisposable
{
    private static readonly Guid TestTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private const string Source = "seeder";

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly InAppNotificationRepository _repository;

    public InAppNotificationRepositoryTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(TestTenantId, "test");
        _context = _db.CreateContext();
        _repository = new InAppNotificationRepository(_context.AsFactory());
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private static InAppNotification Notice(string userId, string? source, string title, bool archived = false) => new()
    {
        Id = Guid.CreateVersion7(),
        UserId = userId,
        Type = "test",
        Title = title,
        Source = source,
        CreatedAt = new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc),
        IsArchived = archived,
        ArchivedAt = archived ? new DateTime(2030, 1, 3, 0, 0, 0, DateTimeKind.Utc) : null,
        ArchiveReason = archived ? NotificationArchiveReason.Dismissed : null,
    };

    private async Task<List<string>> TitlesOfAsync(string userId) =>
        await _context.InAppNotifications.AsNoTracking()
            .Where(n => n.UserId == userId)
            .OrderBy(n => n.Title)
            .Select(n => n.Title)
            .ToListAsync();

    [Fact]
    public async Task ReplaceBySourceAsync_ReplacesOnlyTheUsersRowsOfThatSource_ArchivedIncluded()
    {
        var active = Notice("user", Source, "old active");
        var archived = Notice("user", Source, "old archived", archived: true);
        var otherSource = Notice("user", "real", "other source");
        var otherUser = Notice("other-user", Source, "other user");
        _context.InAppNotifications.AddRange(
            Stored(active), Stored(archived), Stored(otherSource), Stored(otherUser));
        await _context.SaveChangesAsync();

        var readAt = new DateTime(2030, 1, 4, 5, 6, 7, DateTimeKind.Utc);
        var seeded = Notice("user", Source, "seeded") with { ReadAt = readAt };
        await _repository.ReplaceBySourceAsync("user", Source, [seeded]);

        (await TitlesOfAsync("user")).Should().Equal("other source", "seeded");
        (await TitlesOfAsync("other-user")).Should().Equal("other user");
        var stored = await _context.InAppNotifications.AsNoTracking().SingleAsync(n => n.Title == "seeded");
        stored.Id.Should().Be(seeded.Id);
        stored.CreatedAt.Should().Be(seeded.CreatedAt);
        stored.ReadAt.Should().Be(readAt);
    }

    [Fact]
    public async Task ReplaceBySourceAsync_WithNothingToStorePurgesTheSource()
    {
        _context.InAppNotifications.Add(Stored(Notice("user", Source, "old")));
        await _context.SaveChangesAsync();

        await _repository.ReplaceBySourceAsync("user", Source, []);

        (await TitlesOfAsync("user")).Should().BeEmpty();
    }

    private static InAppNotificationEntity Stored(InAppNotification notification) =>
        InAppNotificationMapper.ToEntity(notification);
}

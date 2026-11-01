using FluentAssertions;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Mappers;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// What a re-upload of an activity would find held. An activity is stored as a state span, heart
/// rate, step count or sleep session under its id, so the question spans all four tables.
/// </summary>
[Trait("Category", "Unit")]
public class ActivityDedupRepositoryTests : IDisposable
{
    private static readonly DateTime Stamp = new(2026, 10, 30, 9, 0, 0, DateTimeKind.Utc);

    private readonly Guid _tenant = Guid.CreateVersion7();
    private readonly Guid _otherTenant = Guid.CreateVersion7();
    private readonly SqliteTestDatabase _db;

    public ActivityDedupRepositoryTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(_tenant, "site").SeedTenant(_otherTenant, "other");
    }

    public void Dispose() => _db.Dispose();

    private ActivityDedupRepository Repository(Guid tenant) => new(new TestNocturneDbContextFactory(_db.CreateContext(tenant)));

    private static StateSpanEntity Span(string id) => new()
    {
        Id = Guid.CreateVersion7(), Category = "Exercise", State = "exercise", StartTimestamp = Stamp,
        Source = "connector", OriginalId = id,
    };

    private static HeartRateEntity Heart(string id) => new()
    {
        Id = Guid.CreateVersion7(), Timestamp = Stamp, Bpm = 72, OriginalId = id,
    };

    private static StepCountEntity Steps(string id) => new()
    {
        Id = Guid.CreateVersion7(), Timestamp = Stamp, Metric = 800, OriginalId = id,
    };

    private static SleepSessionEntity Sleep(string id, string source) => new()
    {
        Id = Guid.CreateVersion7(), StartTime = Stamp.AddHours(-8), EndTime = Stamp, Source = source,
        Type = "Overnight", DetectionMethod = "Auto", OriginalId = id,
    };

    private async Task StoreAsync(Guid tenant, params ITenantScoped[] rows)
    {
        await using var db = _db.CreateContext(tenant);
        foreach (var row in rows)
        {
            row.TenantId = tenant;
            db.Add(row);
        }

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task AnIdHeldByAnyOfTheFourTablesIsHeld()
    {
        await StoreAsync(_tenant, Span("span"), Heart("heart"), Steps("steps"),
            Sleep("sleep", ActivityStateSpanMapper.StoredSleepSessionSource));

        var held = await Repository(_tenant).GetHeldIdsAsync(["span", "heart", "steps", "sleep", "absent"]);

        held.Should().BeEquivalentTo(["span", "heart", "steps", "sleep"]);
    }

    [Fact]
    public async Task ASleepSessionOfAnotherSourceDoesNotHoldTheId()
    {
        await StoreAsync(_tenant, Sleep("other-source", "Apple"));

        (await Repository(_tenant).GetHeldIdsAsync(["other-source"])).Should().BeEmpty(
            "a session is keyed by source and original id, so another source's session is a different record");
    }

    [Fact]
    public async Task AUserTombstoneHoldsTheIdAndASystemSweepDoesNot()
    {
        var userDeleted = Heart("user-deleted");
        var swept = Heart("swept");
        userDeleted.DeletedAt = Stamp;
        swept.DeletedAt = Stamp;
        await using (var db = _db.CreateContext(_tenant))
        {
            userDeleted.TenantId = swept.TenantId = _tenant;
            db.AddRange(userDeleted, swept);
            db.Entry(userDeleted).Property("DeletedByUser").CurrentValue = true;
            await db.SaveChangesAsync();
        }

        var held = await Repository(_tenant).GetHeldIdsAsync(["user-deleted", "swept"]);

        held.Should().BeEquivalentTo(["user-deleted"],
            "a delete by the user must not be re-imported; a sweep leaves the record free to be written again");
    }

    [Fact]
    public async Task AnotherTenantsRowsHoldNothing()
    {
        await StoreAsync(_otherTenant, Span("theirs"), Heart("theirs-heart"));

        (await Repository(_tenant).GetHeldIdsAsync(["theirs", "theirs-heart"])).Should().BeEmpty();
    }

    [Fact]
    public async Task NoIdsHoldNothing()
    {
        (await Repository(_tenant).GetHeldIdsAsync([])).Should().BeEmpty();
    }
}

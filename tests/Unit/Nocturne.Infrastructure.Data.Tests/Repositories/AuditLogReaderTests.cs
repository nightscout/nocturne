using FluentAssertions;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary><see cref="AuditLogReader"/> over SQLite: both logs through one window, sort and page.</summary>
[Trait("Category", "Unit")]
public class AuditLogReaderTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly Guid _tenant = Guid.CreateVersion7();
    private readonly Guid _otherTenant = Guid.CreateVersion7();
    private readonly SqliteTestDatabase _db;

    public AuditLogReaderTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(_tenant, "site").SeedTenant(_otherTenant, "other");
    }

    public void Dispose() => _db.Dispose();

    private AuditLogReader Reader(Guid tenant) => new(new TestNocturneDbContextFactory(_db.CreateContext(tenant)));

    private static AuditLogWindow Window(
        int limit = 100, int offset = 0, bool oldestFirst = false, Guid? actor = null, string? entityType = null,
        DateTime? from = null, DateTime? to = null) =>
        new(from ?? Start.AddDays(-1), to ?? Start.AddDays(1), limit, offset, oldestFirst, actor, entityType);

    private static MutationAuditLogEntity Mutation(Guid tenant, int minute, string entityType = "Entry",
        string action = "create", Guid? entityId = null, Guid? actor = null) => new()
    {
        Id = Guid.CreateVersion7(), TenantId = tenant, EntityType = entityType, EntityId = entityId,
        Action = action, ActorSubjectId = actor, CreatedAt = Start.AddMinutes(minute),
    };

    private static ReadAccessLogEntity Read(Guid tenant, int minute, string endpoint = "/api/v4/glucose",
        int status = 200, string? entityType = null, Guid? actor = null) => new()
    {
        Id = Guid.CreateVersion7(), TenantId = tenant, Endpoint = endpoint, StatusCode = status,
        EntityType = entityType, ActorSubjectId = actor, CreatedAt = Start.AddMinutes(minute),
    };

    private async Task StoreAsync(params ITenantScoped[] rows)
    {
        foreach (var row in rows)
        {
            await using var db = _db.CreateContext(row.TenantId);
            db.Add(row);
            await db.SaveChangesAsync();
        }
    }

    private static readonly MutationAuditFilter AnyMutation = new(null, null);
    private static readonly ReadAccessLogFilter AnyRead = new(null, null);

    [Fact]
    public async Task MutationsComeNewestFirstByDefaultAndOldestFirstWhenAsked()
    {
        await StoreAsync(Mutation(_tenant, 1), Mutation(_tenant, 3), Mutation(_tenant, 2));

        var newest = await Reader(_tenant).GetMutationsAsync(Window(), AnyMutation);
        var oldest = await Reader(_tenant).GetMutationsAsync(Window(oldestFirst: true), AnyMutation);

        newest.Data.Select(e => e.CreatedAt).Should().Equal(Start.AddMinutes(3), Start.AddMinutes(2), Start.AddMinutes(1));
        oldest.Data.Select(e => e.CreatedAt).Should().Equal(Start.AddMinutes(1), Start.AddMinutes(2), Start.AddMinutes(3));
    }

    [Fact]
    public async Task ReadsComeNewestFirstByDefaultAndOldestFirstWhenAsked()
    {
        await StoreAsync(Read(_tenant, 1), Read(_tenant, 3), Read(_tenant, 2));

        var newest = await Reader(_tenant).GetReadsAsync(Window(), AnyRead);
        var oldest = await Reader(_tenant).GetReadsAsync(Window(oldestFirst: true), AnyRead);

        newest.Data.Select(e => e.CreatedAt).Should().Equal(Start.AddMinutes(3), Start.AddMinutes(2), Start.AddMinutes(1));
        oldest.Data.Select(e => e.CreatedAt).Should().Equal(Start.AddMinutes(1), Start.AddMinutes(2), Start.AddMinutes(3));
    }

    [Fact]
    public async Task ThePageSkipsAndTakesWhileTheTotalCountsEveryMatch()
    {
        await StoreAsync(Enumerable.Range(0, 5).Select(i => Mutation(_tenant, i)).ToArray());

        var page = await Reader(_tenant).GetMutationsAsync(Window(limit: 2, offset: 1, oldestFirst: true), AnyMutation);

        page.Data.Select(e => e.CreatedAt).Should().Equal(Start.AddMinutes(1), Start.AddMinutes(2));
        page.Pagination.Should().BeEquivalentTo(new { Limit = 2, Offset = 1, Total = 5 });
    }

    [Fact]
    public async Task TheTotalCountsTheFilteredRowsNotTheWholeLog()
    {
        await StoreAsync(Read(_tenant, 1, status: 200), Read(_tenant, 2, status: 500), Read(_tenant, 3, status: 500));

        var page = await Reader(_tenant).GetReadsAsync(Window(limit: 1), new ReadAccessLogFilter(null, 500));

        page.Data.Should().ContainSingle();
        page.Pagination.Total.Should().Be(2);
    }

    [Fact]
    public async Task TheTimeRangeIsInclusiveAtBothEnds()
    {
        await StoreAsync(Mutation(_tenant, -1), Mutation(_tenant, 0), Mutation(_tenant, 10), Mutation(_tenant, 11),
            Read(_tenant, -1), Read(_tenant, 0), Read(_tenant, 10), Read(_tenant, 11));
        var window = Window(from: Start, to: Start.AddMinutes(10));

        (await Reader(_tenant).GetMutationsAsync(window, AnyMutation)).Pagination.Total.Should().Be(2);
        (await Reader(_tenant).GetReadsAsync(window, AnyRead)).Pagination.Total.Should().Be(2);
    }

    [Fact]
    public async Task TheSharedActorAndEntityTypeFiltersApplyToBothLogs()
    {
        var actor = Guid.CreateVersion7();
        await StoreAsync(
            Mutation(_tenant, 1, entityType: "Entry", actor: actor), Mutation(_tenant, 2, entityType: "Entry"),
            Mutation(_tenant, 3, entityType: "Bolus", actor: actor),
            Read(_tenant, 1, entityType: "Entry", actor: actor), Read(_tenant, 2, entityType: "Entry"),
            Read(_tenant, 3, entityType: "Bolus", actor: actor));

        var byActor = Window(actor: actor);
        var byType = Window(entityType: "Entry");
        var both = Window(actor: actor, entityType: "Entry");

        (await Reader(_tenant).GetMutationsAsync(byActor, AnyMutation)).Pagination.Total.Should().Be(2);
        (await Reader(_tenant).GetReadsAsync(byActor, AnyRead)).Pagination.Total.Should().Be(2);
        (await Reader(_tenant).GetMutationsAsync(byType, AnyMutation)).Pagination.Total.Should().Be(2);
        (await Reader(_tenant).GetReadsAsync(byType, AnyRead)).Pagination.Total.Should().Be(2);
        (await Reader(_tenant).GetMutationsAsync(both, AnyMutation)).Data.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { EntityType = "Entry", ActorSubjectId = actor });
        (await Reader(_tenant).GetReadsAsync(both, AnyRead)).Pagination.Total.Should().Be(1);
    }

    [Fact]
    public async Task TheMutationActionAndEntityIdFiltersNarrowTheMutationLog()
    {
        var id = Guid.CreateVersion7();
        await StoreAsync(
            Mutation(_tenant, 1, action: "create", entityId: id), Mutation(_tenant, 2, action: "delete", entityId: id),
            Mutation(_tenant, 3, action: "delete", entityId: Guid.CreateVersion7()));

        (await Reader(_tenant).GetMutationsAsync(Window(), new MutationAuditFilter("delete", null)))
            .Pagination.Total.Should().Be(2);
        (await Reader(_tenant).GetMutationsAsync(Window(), new MutationAuditFilter(null, id)))
            .Pagination.Total.Should().Be(2);
        (await Reader(_tenant).GetMutationsAsync(Window(), new MutationAuditFilter("delete", id)))
            .Data.Should().ContainSingle().Which.Action.Should().Be("delete");
    }

    [Fact]
    public async Task TheReadEndpointAndStatusFiltersNarrowTheReadLog()
    {
        await StoreAsync(
            Read(_tenant, 1, endpoint: "/a", status: 200), Read(_tenant, 2, endpoint: "/a", status: 403),
            Read(_tenant, 3, endpoint: "/b", status: 403));

        (await Reader(_tenant).GetReadsAsync(Window(), new ReadAccessLogFilter("/a", null)))
            .Pagination.Total.Should().Be(2);
        (await Reader(_tenant).GetReadsAsync(Window(), new ReadAccessLogFilter(null, 403)))
            .Pagination.Total.Should().Be(2);
        (await Reader(_tenant).GetReadsAsync(Window(), new ReadAccessLogFilter("/a", 403)))
            .Data.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Endpoint = "/a", StatusCode = 403 });
    }

    [Fact]
    public async Task AnEmptyStringFilterIsNoFilter()
    {
        await StoreAsync(Mutation(_tenant, 1), Read(_tenant, 1));

        (await Reader(_tenant).GetMutationsAsync(Window(entityType: ""), new MutationAuditFilter("", null)))
            .Pagination.Total.Should().Be(1);
        (await Reader(_tenant).GetReadsAsync(Window(entityType: ""), new ReadAccessLogFilter("", null)))
            .Pagination.Total.Should().Be(1);
    }

    [Fact]
    public async Task AMutationEntryKeepsEveryColumnItCarries()
    {
        var actor = Guid.CreateVersion7();
        var row = Mutation(_tenant, 1, actor: actor);
        row.ChangesJson = "{\"a\":1}";
        row.ActorName = "Ada";
        row.AuthType = "oauth";
        row.IpAddress = "10.0.0.1";
        row.Endpoint = "/api/v4/entries";
        await StoreAsync(row);

        var entry = (await Reader(_tenant).GetMutationsAsync(Window(), AnyMutation)).Data.Single();

        entry.Should().BeEquivalentTo(new MutationAuditEntry(
            row.Id, row.CreatedAt, "Entry", null, "create", "{\"a\":1}", actor, "Ada", "oauth", "10.0.0.1", "/api/v4/entries"));
    }

    [Fact]
    public async Task AReadEntryKeepsEveryColumnItCarries()
    {
        var actor = Guid.CreateVersion7();
        var row = Read(_tenant, 1, endpoint: "/api/v4/entries", status: 403, entityType: "Entry", actor: actor);
        row.RecordCount = 17;
        row.QueryParametersJson = "{\"q\":1}";
        row.ActorName = "Ada";
        row.AuthType = "oauth";
        row.IpAddress = "10.0.0.1";
        row.CredentialFingerprint = "fp-123";
        await StoreAsync(row);

        var entry = (await Reader(_tenant).GetReadsAsync(Window(), AnyRead)).Data.Single();

        entry.Should().BeEquivalentTo(new ReadAccessAuditEntry(
            row.Id, row.CreatedAt, "/api/v4/entries", "Entry", 17, 403, "{\"q\":1}", actor, "Ada", "oauth", "10.0.0.1", "fp-123"));
    }

    [Fact]
    public async Task AReadAsOneTenantReturnsNoneOfAnotherTenantsRows()
    {
        await StoreAsync(Mutation(_otherTenant, 1), Read(_otherTenant, 1), Mutation(_tenant, 2), Read(_tenant, 2));

        var mutations = await Reader(_tenant).GetMutationsAsync(Window(), AnyMutation);
        var reads = await Reader(_tenant).GetReadsAsync(Window(), AnyRead);

        mutations.Data.Select(e => e.CreatedAt).Should().Equal(Start.AddMinutes(2));
        mutations.Pagination.Total.Should().Be(1);
        reads.Data.Select(e => e.CreatedAt).Should().Equal(Start.AddMinutes(2));
        reads.Pagination.Total.Should().Be(1);
    }
}

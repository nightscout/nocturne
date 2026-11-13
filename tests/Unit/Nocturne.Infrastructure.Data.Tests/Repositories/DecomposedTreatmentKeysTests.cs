using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// The legacy ids a decomposed treatment's records are stored under, read and moved with another
/// tenant's and another source's rows under the same ids beside them.
/// </summary>
[Trait("Category", "Unit")]
public class DecomposedTreatmentKeysTests : IDisposable
{
    private const string Connector = "connector";
    private const string Uploader = "uploader";
    private static readonly DateTime Stamp = new(2026, 10, 30, 9, 0, 0, DateTimeKind.Utc);

    private readonly Guid _tenant = Guid.CreateVersion7();
    private readonly Guid _otherTenant = Guid.CreateVersion7();
    private readonly SqliteTestDatabase _db;

    public DecomposedTreatmentKeysTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(_tenant, "site").SeedTenant(_otherTenant, "other");
    }

    public void Dispose() => _db.Dispose();

    private DecomposedTreatmentKeys Keys(Guid tenant) => new(new TestNocturneDbContextFactory(_db.CreateContext(tenant)));

    private static BolusEntity Bolus(string legacyId, string source, string? fingerprint = null) => new()
    {
        Id = Guid.CreateVersion7(), Timestamp = Stamp, Insulin = 1, DataSource = source, LegacyId = legacyId,
        UpstreamFingerprint = fingerprint,
    };

    private static NoteEntity Note(string legacyId, string source) => new()
    {
        Id = Guid.CreateVersion7(), Timestamp = Stamp, Text = "note", DataSource = source, LegacyId = legacyId,
    };

    private static CarbIntakeEntity Carbs(string legacyId, string source, string? fingerprint = null) => new()
    {
        Id = Guid.CreateVersion7(), Timestamp = Stamp, Carbs = 10, DataSource = source, LegacyId = legacyId,
        UpstreamFingerprint = fingerprint,
    };

    private static StateSpanEntity Override(string legacyId, string source) => new()
    {
        Id = Guid.CreateVersion7(), Category = nameof(StateSpanCategory.Override), State = "Custom",
        StartTimestamp = Stamp, Source = source, LegacyId = legacyId,
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

    private async Task DeleteAsync(Guid tenant, ITenantScoped row, bool byUser)
    {
        await using var db = _db.CreateContext(tenant);
        row.TenantId = tenant;
        ((ISoftDeletable)row).DeletedAt = Stamp;
        db.Add(row);
        db.Entry(row).Property(NocturneDbContext.DeletedByUserProperty).CurrentValue = byUser;
        await db.SaveChangesAsync();
    }

    private async Task<List<(string? LegacyId, string? Source, bool Deleted, bool ByUser)>> BolusesAsync(Guid tenant)
    {
        await using var db = _db.CreateContext(tenant);
        return [.. (await db.Boluses.IncludingDeleted().ToListAsync())
            .Select(b => (b.LegacyId, b.DataSource, b.DeletedAt != null,
                db.Entry(b).Property<bool>(NocturneDbContext.DeletedByUserProperty).CurrentValue))];
    }

    [Fact]
    public async Task AnIdIsHeldByALiveRowOfAnySourceOrAUserTombstoneAndNotBySweptRowsOrAnotherTenant()
    {
        await StoreAsync(_tenant, Bolus("live", Uploader), Override("span", Connector));
        await DeleteAsync(_tenant, Note("user-deleted", Connector), byUser: true);
        await DeleteAsync(_tenant, Bolus("swept", Connector), byUser: false);
        await DeleteAsync(_tenant, Override("span-deleted", Connector), byUser: true);
        await StoreAsync(_otherTenant, Bolus("theirs", Connector));

        var held = await Keys(_tenant).GetHeldLegacyIdsAsync(
            ["live", "span", "user-deleted", "swept", "span-deleted", "theirs", "absent"]);

        held.Held.Should().BeEquivalentTo(["live", "span", "user-deleted", "span-deleted"]);
        held.DeletedByUser.Should().BeEquivalentTo(["user-deleted", "span-deleted"]);
    }

    [Fact]
    public async Task ALiveRowBesideAUserTombstoneHoldsTheIdAsStored()
    {
        await StoreAsync(_tenant, Bolus("both", Connector));
        await DeleteAsync(_tenant, Note("both", Connector), byUser: true);

        var held = await Keys(_tenant).GetHeldLegacyIdsAsync(["both"]);

        held.Held.Should().Equal("both");
        held.DeletedByUser.Should().BeEmpty("the live row governs a re-upload of its id");
    }

    [Fact]
    public async Task ARekeyMovesOnlyTheSourcesLiveRowsInTheTenant()
    {
        await StoreAsync(_tenant, Bolus("client", Connector), Carbs("client", Uploader), Override("client", Connector));
        await StoreAsync(_otherTenant, Bolus("client", Connector));

        var rekey = await Keys(_tenant).RekeyClientIdsAsync(Connector, [new ClientIdEquivalent("client", "server", Stamp.ToMills())]);

        rekey.Should().Be(new ClientIdRekey(2, Written: true));
        (await BolusesAsync(_tenant)).Should().Equal(("server", Connector, false, false));
        (await BolusesAsync(_otherTenant)).Should().Equal(("client", Connector, false, false));
        await using var db = _db.CreateContext(_tenant);
        (await db.StateSpans.Select(s => s.LegacyId).ToListAsync()).Should().Equal("server");
        (await db.CarbIntakes.Select(c => c.LegacyId).ToListAsync()).Should().Equal("client");
    }

    [Fact]
    public async Task ARekeyCopiesAUserTombstoneOntoEveryEquivalentAndLeavesItInPlace()
    {
        await DeleteAsync(_tenant, Bolus("client", Connector), byUser: true);
        await DeleteAsync(_otherTenant, Bolus("client", Connector), byUser: true);

        var rekey = await Keys(_tenant).RekeyClientIdsAsync(Connector,
        [
            new ClientIdEquivalent("client", "first", Stamp.ToMills()),
            new ClientIdEquivalent("client", "second", Stamp.AddMinutes(5).ToMills()),
        ]);

        rekey.Should().Be(new ClientIdRekey(0, Written: true));
        (await BolusesAsync(_tenant)).Should().BeEquivalentTo(
        [
            ("client", Connector, true, true), ("first", Connector, true, true), ("second", Connector, true, true),
        ]);
        (await BolusesAsync(_otherTenant)).Should().Equal(("client", Connector, true, true));
    }

    [Fact]
    public async Task ARekeyNeitherMovesNorCopiesAnotherSourcesTombstoneUnderTheClientId()
    {
        await DeleteAsync(_tenant, Bolus("client", Connector), byUser: true);
        await DeleteAsync(_tenant, Bolus("client", Uploader), byUser: true);

        var rekey = await Keys(_tenant).RekeyClientIdsAsync(Connector, [new ClientIdEquivalent("client", "server", Stamp.ToMills())]);

        rekey.Should().Be(new ClientIdRekey(0, Written: true));
        (await BolusesAsync(_tenant)).Should().BeEquivalentTo(
        [
            ("client", Connector, true, true), ("client", Uploader, true, true), ("server", Connector, true, true),
        ]);
    }

    [Fact]
    public async Task ARetiredSpanIsNeitherCopiedNorHoldsTheIdItIsUnder()
    {
        var retired = Override("client", Connector);
        retired.Category = "RetiredCategory";
        await DeleteAsync(_tenant, retired, byUser: true);
        var retiredAtServer = Override("server", Uploader);
        retiredAtServer.Category = "RetiredCategory";
        await DeleteAsync(_tenant, retiredAtServer, byUser: true);
        await StoreAsync(_tenant, Bolus("client", Connector));

        var rekey = await Keys(_tenant).RekeyClientIdsAsync(Connector, [new ClientIdEquivalent("client", "server", Stamp.ToMills())]);

        rekey.Should().Be(new ClientIdRekey(1, Written: true));
        (await BolusesAsync(_tenant)).Should().Equal(("server", Connector, false, false));
        await using var db = _db.CreateContext(_tenant);
        (await db.StateSpans.IncludingDeleted().Select(s => s.LegacyId).ToListAsync())
            .Should().BeEquivalentTo(["client", "server"], "the retired span stays where it was and is not copied");
    }

    [Fact]
    public async Task ARekeyLeavesAnIdSomethingAlreadyHolds()
    {
        await StoreAsync(_tenant, Bolus("client", Connector), Note("server", Uploader));

        var rekey = await Keys(_tenant).RekeyClientIdsAsync(Connector, [new ClientIdEquivalent("client", "server", Stamp.ToMills())]);

        rekey.Should().Be(new ClientIdRekey(0, Written: false));
        (await BolusesAsync(_tenant)).Should().Equal(("client", Connector, false, false));
    }

    [Fact]
    public async Task FingerprintsAreReadAndStampedForTheSourceInTheTenantOnly()
    {
        await StoreAsync(_tenant, Bolus("id", Connector, "old"), Note("id", Connector), Carbs("id", Uploader, "theirs"));
        await StoreAsync(_otherTenant, Bolus("id", Connector));
        var keys = Keys(_tenant);

        (await keys.GetFingerprintsFromSourceAsync(Connector, ["id"]))["id"].Should().BeEquivalentTo(["old", null]);

        await keys.StampUnfingerprintedAsync(Connector, new Dictionary<string, string> { ["id"] = "new" });

        await using var db = _db.CreateContext(_tenant);
        (await db.Notes.Select(n => n.UpstreamFingerprint).ToListAsync()).Should().Equal("new");
        (await db.Boluses.Select(b => b.UpstreamFingerprint).ToListAsync()).Should().Equal("old");
        (await db.CarbIntakes.Select(c => c.UpstreamFingerprint).ToListAsync()).Should().Equal("theirs");
        await using var other = _db.CreateContext(_otherTenant);
        (await other.Boluses.Select(b => b.UpstreamFingerprint).ToListAsync()).Should().Equal([(string?)null]);
    }
}

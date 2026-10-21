using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

namespace Nocturne.Infrastructure.Data.Tests.V4Goldens;

/// <summary>
/// <see cref="IDeviceStatusGroupRepository"/> against real Postgres: one status per correlation
/// group, placed by its anchor, the same set for a page, the count and the history.
/// </summary>
[Trait("Category", "Integration")]
[Collection("V4 goldens")]
public class DeviceStatusGroupRepositoryTests
{
    private static readonly DateTime T0 = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly V4GoldenFixture _fx;

    public DeviceStatusGroupRepositoryTests(V4GoldenFixture fx) => _fx = fx;

    [Fact]
    public async Task EveryKindOfStatus_IsOneStatusWithAllItsRows()
    {
        using var scope = await _fx.BeginTenantScopeAsync(Guid.NewGuid());
        var r = new Repos(scope);
        var full = await r.StatusAsync(T0, aps: true, pump: true, uploader: true, extras: true);
        var pumpLed = await r.StatusAsync(T0.AddMinutes(-1), pump: true, uploader: true, extras: true);
        var uploaderOnly = await r.StatusAsync(T0.AddMinutes(-2), uploader: true);
        var extrasOnly = await r.StatusAsync(T0.AddMinutes(-3), extras: true);
        var uncorrelatedPump = await r.Pumps.CreateAsync(
            new PumpSnapshot { Timestamp = T0.AddMinutes(-4), Device = "synthetic://a", DataSource = "test" },
            WriteOrigin.Backfill);

        var page = await r.Groups.GetAsync(null, null, null, limit: 10, offset: 0, descending: true);

        page.Select(Shape).Should().Equal(
            (full, "APUE"),
            (pumpLed, "PUE"),
            (uploaderOnly, "U"),
            (extrasOnly, "E"),
            (uncorrelatedPump.CorrelationId, "P"));
        (await r.Groups.CountAsync(null, null, null)).Should().Be(5);
    }

    [Fact]
    public async Task Paging_CoversEachStatusOnce_EvenWhenTheyShareATimestamp()
    {
        using var scope = await _fx.BeginTenantScopeAsync(Guid.NewGuid());
        var r = new Repos(scope);
        var ids = new[]
        {
            await r.StatusAsync(T0, aps: true),
            await r.StatusAsync(T0, pump: true),
            await r.StatusAsync(T0, uploader: true),
            await r.StatusAsync(T0, extras: true),
        };

        var paged = new List<Guid?>();
        for (var offset = 0; offset < 5; offset++)
            paged.AddRange((await r.Groups.GetAsync(null, null, null, limit: 1, offset, descending: true)).Select(g => Shape(g).Correlation));

        paged.Should().BeEquivalentTo(ids).And.OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task GetOldestAsync_PagesFromTheOldestStatus_WhicheverTableAnchorsIt()
    {
        using var scope = await _fx.BeginTenantScopeAsync(Guid.NewGuid());
        var r = new Repos(scope);
        var newest = await r.StatusAsync(T0, aps: true);
        var newer = await r.StatusAsync(T0.AddMinutes(-1), pump: true);
        var older = await r.StatusAsync(T0.AddMinutes(-2), uploader: true);
        var oldest = await r.StatusAsync(T0.AddMinutes(-3), extras: true);

        var firstPage = await r.Groups.GetAsync(null, null, null, limit: 2, offset: 0, descending: false);
        var secondPage = await r.Groups.GetAsync(null, null, null, limit: 2, offset: 2, descending: false);

        firstPage.Select(g => Shape(g).Correlation).Should().Equal(oldest, older);
        secondPage.Select(g => Shape(g).Correlation).Should().Equal(newer, newest);
    }

    [Fact]
    public async Task WindowAndDevice_ApplyToTheAnchor()
    {
        using var scope = await _fx.BeginTenantScopeAsync(Guid.NewGuid());
        var r = new Repos(scope);
        var correlation = Guid.NewGuid();
        await r.Aps.CreateAsync(
            new ApsSnapshot { Timestamp = T0.AddHours(-2), CorrelationId = correlation, Device = "synthetic://a", DataSource = "test" },
            WriteOrigin.Backfill);
        await r.Pumps.CreateAsync(
            new PumpSnapshot { Timestamp = T0, CorrelationId = correlation, Device = "synthetic://b", DataSource = "test" },
            WriteOrigin.Backfill);
        await r.StatusAsync(T0, extras: true);

        (await r.Groups.CountAsync(T0.AddHours(-1), null, null)).Should().Be(1, "only the extras-only status is anchored in the window");
        (await r.Groups.CountAsync(null, T0.AddHours(-1), null)).Should().Be(1);
        (await r.Groups.CountAsync(null, null, "synthetic://b")).Should().Be(0);
        (await r.Groups.CountAsync(null, null, "synthetic://a")).Should().Be(1, "extras carry no device");
        (await r.Groups.CountAsync(T0, T0, null)).Should().Be(1);
    }

    [Fact]
    public async Task History_CarriesEveryKindOfStatus()
    {
        using var scope = await _fx.BeginTenantScopeAsync(Guid.NewGuid());
        var r = new Repos(scope);
        var uploaderOnly = await r.StatusAsync(T0, uploader: true);
        var extrasOnly = await r.StatusAsync(T0.AddMinutes(-1), extras: true);
        var pumpLed = await r.StatusAsync(T0.AddMinutes(-2), pump: true, uploader: true);

        var history = await r.Groups.GetModifiedSinceAsync(0, 100);

        history.Select(h => (Shape(h.Record), h.Deleted)).Should().BeEquivalentTo(new[]
        {
            ((uploaderOnly, "U"), false),
            ((extrasOnly, "E"), false),
            ((pumpLed, "PU"), false),
        });
    }

    [Fact]
    public async Task History_ReadsAStatusAsDeletedOnlyOnceEveryRowIs()
    {
        using var scope = await _fx.BeginTenantScopeAsync(Guid.NewGuid());
        var r = new Repos(scope);
        var correlation = await r.StatusAsync(T0, aps: true, pump: true);
        var aps = (await r.Aps.GetByCorrelationIdsAsync([correlation!.Value])).Single();
        var pump = (await r.Pumps.GetByCorrelationIdsAsync([correlation.Value])).Single();

        await r.Aps.DeleteAsync(aps.Id, WriteOrigin.Backfill);

        (await r.Groups.GetModifiedSinceAsync(0, 100)).Select(h => (Shape(h.Record), h.Deleted))
            .Should().Equal(((correlation, "P"), false));
        (await r.Groups.GetAsync(null, null, null, 10, 0, descending: true)).Select(Shape).Should().Equal((correlation, "P"));

        await r.Pumps.DeleteAsync(pump.Id, WriteOrigin.Backfill);

        (await r.Groups.GetModifiedSinceAsync(0, 100)).Select(h => (Shape(h.Record), h.Deleted))
            .Should().Equal(((correlation, "AP"), true));
        (await r.Groups.CountAsync(null, null, null)).Should().Be(0);
    }

    [Fact]
    public async Task Ties_AreOrderedByAnchorIdNewestFirst()
    {
        using var scope = await _fx.BeginTenantScopeAsync(Guid.NewGuid());
        var r = new Repos(scope);
        await r.StatusAsync(T0, aps: true);
        await r.StatusAsync(T0, pump: true);
        await r.StatusAsync(T0, pump: true);
        await r.StatusAsync(T0, uploader: true);
        await r.StatusAsync(T0, extras: true);

        var whole = (await r.Groups.GetAsync(null, null, null, limit: 10, offset: 0, descending: true)).Select(AnchorId).ToList();
        var paged = new List<Guid>();
        for (var offset = 0; offset < 5; offset++)
            paged.AddRange((await r.Groups.GetAsync(null, null, null, limit: 1, offset, descending: true)).Select(AnchorId));

        whole.Should().HaveCount(5).And.BeInDescendingOrder();
        paged.Should().Equal(whole);
    }

    [Fact]
    public async Task History_ReportsAStatusWhoseAnchorMovesToAnOlderRow()
    {
        using var scope = await _fx.BeginTenantScopeAsync(Guid.NewGuid());
        var r = new Repos(scope);
        var correlation = await r.StatusAsync(T0, aps: true, pump: true);
        var created = (await r.Groups.GetModifiedSinceAsync(0, 100)).Single().Record.ModifiedAt;
        var cursor = new DateTimeOffset(created, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var aps = (await r.Aps.GetByCorrelationIdsAsync([correlation!.Value])).Single();
        var pump = (await r.Pumps.GetByCorrelationIdsAsync([correlation.Value])).Single();
        await Task.Delay(5);

        await r.Aps.DeleteAsync(aps.Id, WriteOrigin.Backfill);

        var afterApsRemoved = (await r.Groups.GetModifiedSinceAsync(cursor, 100)).Should().ContainSingle().Subject;
        (Shape(afterApsRemoved.Record), afterApsRemoved.Deleted).Should().Be(((correlation, "P"), false));
        afterApsRemoved.Record.ModifiedAt.Should().BeAfter(created);
        cursor = new DateTimeOffset(afterApsRemoved.Record.ModifiedAt, TimeSpan.Zero).ToUnixTimeMilliseconds();
        await Task.Delay(5);

        await r.Pumps.DeleteAsync(pump.Id, WriteOrigin.Backfill);

        var afterPumpRemoved = (await r.Groups.GetModifiedSinceAsync(cursor, 100)).Should().ContainSingle().Subject;
        (Shape(afterPumpRemoved.Record), afterPumpRemoved.Deleted).Should().Be(((correlation, "AP"), true));
    }

    [Fact]
    public async Task History_PagesEachStatusOnceAcrossPages()
    {
        using var scope = await _fx.BeginTenantScopeAsync(Guid.NewGuid());
        var r = new Repos(scope);
        var statuses = new List<Guid?>();
        for (var i = 0; i < 7; i++)
            statuses.Add(await r.StatusAsync(T0.AddMinutes(i), aps: true, pump: true, uploader: i % 2 == 0, extras: i % 3 == 0));
        for (var i = 0; i < 3; i++)
            statuses.Add(await r.StatusAsync(T0.AddMinutes(10 + i), uploader: true));

        var seen = new List<Guid?>();
        long cursor = 0;
        for (var page = 0; page < 20; page++)
        {
            var read = await r.Groups.GetModifiedSinceAsync(cursor, 2);
            if (read.Count == 0)
                break;
            read.Select(h => h.Record.ModifiedAt).Should().BeInAscendingOrder();
            seen.AddRange(read.Select(h => Shape(h.Record).Correlation));
            cursor = new DateTimeOffset(read.Max(h => h.Record.ModifiedAt), TimeSpan.Zero).ToUnixTimeMilliseconds();
        }

        seen.Should().BeEquivalentTo(statuses).And.OnlyHaveUniqueItems();
    }

    private static Guid AnchorId(DeviceStatusGroup g) =>
        g.Aps?.Id ?? g.Pump?.Id ?? g.Uploader?.Id ?? g.Extras!.Id;

    private static (Guid? Correlation, string Rows) Shape(DeviceStatusGroup g) =>
        (g.Aps?.CorrelationId ?? g.Pump?.CorrelationId ?? g.Uploader?.CorrelationId ?? g.Extras?.CorrelationId,
            (g.Aps is null ? "" : "A") + (g.Pump is null ? "" : "P") + (g.Uploader is null ? "" : "U")
            + (g.Extras is null ? "" : "E"));

    private sealed class Repos(IServiceScope scope)
    {
        public IDeviceStatusGroupRepository Groups { get; } = scope.ServiceProvider.GetRequiredService<IDeviceStatusGroupRepository>();
        public IApsSnapshotRepository Aps { get; } = scope.ServiceProvider.GetRequiredService<IApsSnapshotRepository>();
        public IPumpSnapshotRepository Pumps { get; } = scope.ServiceProvider.GetRequiredService<IPumpSnapshotRepository>();
        public IUploaderSnapshotRepository Uploaders { get; } = scope.ServiceProvider.GetRequiredService<IUploaderSnapshotRepository>();
        public IDeviceStatusExtrasRepository Extras { get; } = scope.ServiceProvider.GetRequiredService<IDeviceStatusExtrasRepository>();

        public async Task<Guid?> StatusAsync(
            DateTime at, bool aps = false, bool pump = false, bool uploader = false, bool extras = false)
        {
            var correlation = Guid.NewGuid();
            if (aps)
                await Aps.CreateAsync(
                    new ApsSnapshot { Timestamp = at, CorrelationId = correlation, Device = "synthetic://a", DataSource = "test" },
                    WriteOrigin.Backfill);
            if (pump)
                await Pumps.CreateAsync(
                    new PumpSnapshot { Timestamp = at, CorrelationId = correlation, Device = "synthetic://a", DataSource = "test" },
                    WriteOrigin.Backfill);
            if (uploader)
                await Uploaders.CreateAsync(
                    new UploaderSnapshot { Timestamp = at, CorrelationId = correlation, Device = "synthetic://a", DataSource = "test" },
                    WriteOrigin.Backfill);
            if (extras)
                await Extras.CreateAsync(
                    new DeviceStatusExtras
                    {
                        Timestamp = at,
                        CorrelationId = correlation,
                        Extras = new Dictionary<string, object?> { ["xdripjs"] = new { state = 6 } },
                    },
                    WriteOrigin.Backfill);
            return correlation;
        }
    }
}

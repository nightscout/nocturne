using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Glooko.Xt;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Xt;

public class GlookoXtSyncPathTests
{
    [Fact]
    public async Task Fetch_OpensOneSession_ReadsRecordsWeekly_AndTheExportFortnightly()
    {
        var client = new FakeGlookoXtDataClient();
        var path = new GlookoXtSyncPath(client, NullLogger.Instance);
        var to = DateTime.UtcNow; var from = to.AddDays(-20);

        var fetched = await path.FetchAsync("jwt", from, to, [SyncDataType.Glucose, SyncDataType.StateSpans], null, CancellationToken.None);

        client.Connections.Should().Be(1);
        client.Windows.Should().HaveCount(3, "twenty days a week at a time");
        client.ExportWindows.Should().HaveCount(2, "twenty-two padded days a fortnight at a time");
        client.Disposed.Should().BeTrue();
        fetched.Exports.Should().HaveCount(2);
    }

    [Fact]
    public async Task Fetch_NarrowedToRecordTypes_SkipsTheExport()
    {
        var client = new FakeGlookoXtDataClient();
        var path = new GlookoXtSyncPath(client, NullLogger.Instance);

        await path.FetchAsync("jwt", DateTime.UtcNow.AddDays(-1), DateTime.UtcNow, [SyncDataType.Glucose, SyncDataType.Boluses], null, CancellationToken.None);

        client.ExportWindows.Should().BeEmpty();
    }

    [Fact]
    public async Task Fetch_DedupesARecordTwoWindowsBothReturned()
    {
        var client = new FakeGlookoXtDataClient();
        var at = DateTime.UtcNow.AddDays(-7);
        client.Records = [new GlookoXtRecord { Id = 9, RecordedAt = at.ToString("O"), Carbs = 12 }];
        var path = new GlookoXtSyncPath(client, NullLogger.Instance);
        var now = DateTime.UtcNow;

        var fetched = await path.FetchAsync("jwt", now.AddDays(-14), now, [SyncDataType.CarbIntake], null, CancellationToken.None);

        client.Windows.Should().HaveCount(2);
        fetched.Records.Should().ContainSingle();
    }

    [Fact]
    public async Task Map_TakesTheUnitTheAccountReports()
    {
        var client = new FakeGlookoXtDataClient
        {
            Account = new GlookoXtAccount { BloodGlucoseUnit = "mmol/l" },
            // 20 alone would read as mg/dL on the value heuristic; the account says mmol/L.
            Records = [new GlookoXtRecord { Id = 1, RecordedAt = DateTime.UtcNow.AddHours(-1).ToString("O"), GlycemiaCgm = 20 }],
        };
        var path = new GlookoXtSyncPath(client, NullLogger.Instance);

        var fetched = await path.FetchAsync("jwt", DateTime.UtcNow.AddDays(-1), DateTime.UtcNow, [SyncDataType.Glucose], null, CancellationToken.None);
        var batch = path.Map(fetched);

        batch.SensorGlucose.Should().ContainSingle().Which.Mgdl.Should().Be(360);
    }

    [Fact]
    public async Task Fetch_WhenTheServerRefusesTheToken_Throws()
    {
        var client = new FakeGlookoXtDataClient { ConnectFailure = new GlookoXtAuthenticationException("refused") };
        var path = new GlookoXtSyncPath(client, NullLogger.Instance);

        var act = () => path.FetchAsync("jwt", DateTime.UtcNow.AddDays(-1), DateTime.UtcNow, [SyncDataType.Glucose], null, CancellationToken.None);

        await act.Should().ThrowAsync<GlookoXtAuthenticationException>();
    }
}

internal sealed class FakeGlookoXtDataClient : IGlookoXtDataClient, IGlookoXtSession
{
    public List<GlookoXtRecord> Records { get; set; } = [];
    public List<(DateTime From, DateTime To)> Windows { get; } = [];
    public List<(DateOnly From, DateOnly To)> ExportWindows { get; } = [];
    public GlookoXtExport Export { get; set; } = new();
    public GlookoXtAccount? Account { get; set; }
    public int Connections { get; private set; }
    public bool Disposed { get; private set; }
    public Exception? ConnectFailure { get; set; }

    public Task<IGlookoXtSession> ConnectAsync(string serverUrl, string token, CancellationToken ct)
    {
        Connections++;
        if (ConnectFailure is not null) throw ConnectFailure;
        return Task.FromResult<IGlookoXtSession>(this);
    }

    public Task<IReadOnlyList<GlookoXtRecord>> GetCollectedDataAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        Windows.Add((fromUtc, toUtc));
        IReadOnlyList<GlookoXtRecord> inWindow = Records
            .Where(r => DateTime.Parse(r.RecordedAt!, null, System.Globalization.DateTimeStyles.AdjustToUniversal) is var at
                        && at >= fromUtc.AddDays(-1) && at <= toUtc.AddDays(1))
            .ToList();
        return Task.FromResult(inWindow);
    }

    public Task<IReadOnlyList<GlookoXtProduct>> GetProductsAsync(string productType, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<GlookoXtProduct>>(productType == "pump" ? [new GlookoXtProduct { Id = 97, Name = "Ypsomed YpsoPump" }] : []);

    public Task<GlookoXtExport> ExportRecordsAsync(DateOnly firstDay, DateOnly lastDay, CancellationToken ct)
    {
        ExportWindows.Add((firstDay, lastDay));
        return Task.FromResult(Export);
    }

    public Task<GlookoXtAccount?> GetAccountAsync(CancellationToken ct) => Task.FromResult(Account);

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

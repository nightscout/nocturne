using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.Connectors.HomeAssistant.Configurations;
using Nocturne.Connectors.HomeAssistant.Services;
using Nocturne.Connectors.HomeAssistant.WriteBack;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Connectors.HomeAssistant.Tests.WriteBack;

public class HomeAssistantWriteBackSinkTests
{
    private readonly Mock<IHomeAssistantApiClient> _apiClientMock = new();
    private readonly Mock<IApsSnapshotRepository> _apsSnapshotRepoMock = new();
    private readonly Mock<ILogger<HomeAssistantWriteBackSink>> _loggerMock = new();

    private HomeAssistantWriteBackSink CreateSink(
        bool writeBackEnabled = true,
        HashSet<WriteBackDataType>? writeBackTypes = null)
    {
        var config = new HomeAssistantConnectorConfiguration
        {
            WriteBackEnabled = writeBackEnabled,
            WriteBackTypes = writeBackTypes ?? [WriteBackDataType.Glucose]
        };

        return new HomeAssistantWriteBackSink(
            _apiClientMock.Object, config, _apsSnapshotRepoMock.Object, _loggerMock.Object);
    }

    private static Entry CreateRecentEntry(double sgv = 120, string direction = "Flat")
    {
        return new Entry
        {
            Mills = DateTimeOffset.UtcNow.AddSeconds(-30).ToUnixTimeMilliseconds(),
            Sgv = sgv,
            Direction = direction
        };
    }

    private static Entry CreateStaleEntry()
    {
        return new Entry
        {
            Mills = DateTimeOffset.UtcNow.AddMinutes(-15).ToUnixTimeMilliseconds(),
            Sgv = 100,
            Direction = "Flat"
        };
    }

    private void SetupApsSnapshot(ApsSnapshot? snapshot)
    {
        var snapshots = snapshot != null
            ? new List<ApsSnapshot> { snapshot }
            : new List<ApsSnapshot>();

        _apsSnapshotRepoMock
            .Setup(x => x.GetAsync(null, null, null, null, 1, 0, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshots);
    }

    [Fact]
    public async Task OnCreatedAsync_WhenWriteBackDisabled_DoesNothing()
    {
        var sink = CreateSink(writeBackEnabled: false);
        var entry = CreateRecentEntry();

        await sink.OnCreatedAsync(entry);

        _apiClientMock.Verify(
            x => x.SetStateAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<Dictionary<string, object>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task OnCreatedAsync_WhenEntryFromHomeAssistant_SkipsToPreventSyncLoop()
    {
        var sink = CreateSink();
        var entry = CreateRecentEntry();
        entry.DataSource = DataSources.HomeAssistantConnector;

        await sink.OnCreatedAsync(entry);

        _apiClientMock.Verify(
            x => x.SetStateAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<Dictionary<string, object>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task OnCreatedAsync_WhenStaleEntry_Skips()
    {
        var sink = CreateSink();
        var entry = CreateStaleEntry();

        await sink.OnCreatedAsync(entry);

        _apiClientMock.Verify(
            x => x.SetStateAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<Dictionary<string, object>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task OnCreatedAsync_PushesGlucoseWithCorrectAttributes()
    {
        _apiClientMock
            .Setup(x => x.SetStateAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<Dictionary<string, object>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var sink = CreateSink(writeBackTypes: [WriteBackDataType.Glucose]);
        var entry = CreateRecentEntry(145, "FortyFiveUp");

        await sink.OnCreatedAsync(entry);

        _apiClientMock.Verify(
            x => x.SetStateAsync(
                "sensor.nocturne_glucose",
                It.Is<string>(s => s.StartsWith("145")),
                It.Is<Dictionary<string, object>>(d =>
                    d["unit_of_measurement"].Equals("mg/dL") &&
                    d["trend"].Equals("FortyFiveUp")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task OnCreatedAsync_WhenGlucoseNotInWriteBackTypes_SkipsGlucose()
    {
        SetupApsSnapshot(new ApsSnapshot { Iob = 2.5 });

        _apiClientMock
            .Setup(x => x.SetStateAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<Dictionary<string, object>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var sink = CreateSink(writeBackTypes: [WriteBackDataType.Iob]);
        var entry = CreateRecentEntry();

        await sink.OnCreatedAsync(entry);

        _apiClientMock.Verify(
            x => x.SetStateAsync(
                "sensor.nocturne_glucose",
                It.IsAny<string>(),
                It.IsAny<Dictionary<string, object>>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task OnCreatedAsync_IndividualFailureDoesNotBlockOthers()
    {
        SetupApsSnapshot(new ApsSnapshot { Iob = 1.0 });

        _apiClientMock
            .Setup(x => x.SetStateAsync(
                "sensor.nocturne_glucose",
                It.IsAny<string>(),
                It.IsAny<Dictionary<string, object>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        _apiClientMock
            .Setup(x => x.SetStateAsync(
                "sensor.nocturne_iob",
                It.IsAny<string>(),
                It.IsAny<Dictionary<string, object>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var sink = CreateSink(writeBackTypes:
        [
            WriteBackDataType.Glucose,
            WriteBackDataType.Iob
        ]);
        var entry = CreateRecentEntry();

        // Should not throw even though glucose push fails
        var act = () => sink.OnCreatedAsync(entry);
        await act.Should().NotThrowAsync();

        // IOB should still be pushed despite glucose failure
        _apiClientMock.Verify(
            x => x.SetStateAsync(
                "sensor.nocturne_iob",
                It.IsAny<string>(),
                It.IsAny<Dictionary<string, object>>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task OnCreatedAsync_BatchUsesLatestEntry()
    {
        _apiClientMock
            .Setup(x => x.SetStateAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<Dictionary<string, object>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var sink = CreateSink(writeBackTypes: [WriteBackDataType.Glucose]);

        var older = new Entry
        {
            Mills = DateTimeOffset.UtcNow.AddSeconds(-60).ToUnixTimeMilliseconds(),
            Sgv = 100,
            Direction = "Flat"
        };
        var latest = new Entry
        {
            Mills = DateTimeOffset.UtcNow.AddSeconds(-10).ToUnixTimeMilliseconds(),
            Sgv = 180,
            Direction = "SingleUp"
        };

        await sink.OnCreatedAsync(new List<Entry> { older, latest });

        _apiClientMock.Verify(
            x => x.SetStateAsync(
                "sensor.nocturne_glucose",
                "180",
                It.Is<Dictionary<string, object>>(d => d["trend"].Equals("SingleUp")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task OnCreatedAsync_WhenIobEnabled_PushesIobFromApsSnapshot()
    {
        SetupApsSnapshot(new ApsSnapshot { Iob = 2.55 });

        _apiClientMock
            .Setup(x => x.SetStateAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<Dictionary<string, object>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var sink = CreateSink(writeBackTypes: [WriteBackDataType.Iob]);
        var entry = CreateRecentEntry();

        await sink.OnCreatedAsync(entry);

        _apiClientMock.Verify(
            x => x.SetStateAsync(
                "sensor.nocturne_iob",
                "2.55",
                It.Is<Dictionary<string, object>>(d =>
                    d["unit_of_measurement"].Equals("U") &&
                    d["friendly_name"].Equals("Nocturne IOB")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task OnCreatedAsync_WhenCobEnabled_PushesCobFromApsSnapshot()
    {
        SetupApsSnapshot(new ApsSnapshot { Cob = 45.3 });

        _apiClientMock
            .Setup(x => x.SetStateAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<Dictionary<string, object>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var sink = CreateSink(writeBackTypes: [WriteBackDataType.Cob]);
        var entry = CreateRecentEntry();

        await sink.OnCreatedAsync(entry);

        _apiClientMock.Verify(
            x => x.SetStateAsync(
                "sensor.nocturne_cob",
                "45.3",
                It.Is<Dictionary<string, object>>(d =>
                    d["unit_of_measurement"].Equals("g") &&
                    d["friendly_name"].Equals("Nocturne COB")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task OnCreatedAsync_WhenPredictedBgEnabled_PushesEventualBg()
    {
        SetupApsSnapshot(new ApsSnapshot
        {
            PredictedDefaultJson = "[120.0, 115.0, 110.0, 105.0, 100.0]"
        });

        _apiClientMock
            .Setup(x => x.SetStateAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<Dictionary<string, object>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var sink = CreateSink(writeBackTypes: [WriteBackDataType.PredictedBg]);
        var entry = CreateRecentEntry();

        await sink.OnCreatedAsync(entry);

        _apiClientMock.Verify(
            x => x.SetStateAsync(
                "sensor.nocturne_predicted_bg",
                "100",
                It.Is<Dictionary<string, object>>(d =>
                    d["unit_of_measurement"].Equals("mg/dL") &&
                    d["prediction_points"].Equals(5)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task OnCreatedAsync_WhenLoopStatusEnabled_PushesEnactedState()
    {
        SetupApsSnapshot(new ApsSnapshot
        {
            Enacted = true,
            EnactedRate = 1.5,
            EnactedDuration = 30
        });

        _apiClientMock
            .Setup(x => x.SetStateAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<Dictionary<string, object>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var sink = CreateSink(writeBackTypes: [WriteBackDataType.LoopStatus]);
        var entry = CreateRecentEntry();

        await sink.OnCreatedAsync(entry);

        _apiClientMock.Verify(
            x => x.SetStateAsync(
                "sensor.nocturne_loop_status",
                "enacted",
                It.Is<Dictionary<string, object>>(d =>
                    d["enacted_rate"].Equals(1.5) &&
                    d["enacted_duration"].Equals(30)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task OnCreatedAsync_WhenLoopNotEnacted_PushesOpenState()
    {
        SetupApsSnapshot(new ApsSnapshot { Enacted = false });

        _apiClientMock
            .Setup(x => x.SetStateAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<Dictionary<string, object>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var sink = CreateSink(writeBackTypes: [WriteBackDataType.LoopStatus]);
        var entry = CreateRecentEntry();

        await sink.OnCreatedAsync(entry);

        _apiClientMock.Verify(
            x => x.SetStateAsync(
                "sensor.nocturne_loop_status",
                "open",
                It.IsAny<Dictionary<string, object>>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task OnCreatedAsync_WhenNoApsSnapshot_SkipsComputedPushes()
    {
        SetupApsSnapshot(null);

        var sink = CreateSink(writeBackTypes:
        [
            WriteBackDataType.Iob,
            WriteBackDataType.Cob,
            WriteBackDataType.PredictedBg,
            WriteBackDataType.LoopStatus
        ]);
        var entry = CreateRecentEntry();

        await sink.OnCreatedAsync(entry);

        // IOB, COB, PredictedBg should not be pushed (null values)
        _apiClientMock.Verify(
            x => x.SetStateAsync(
                "sensor.nocturne_iob",
                It.IsAny<string>(),
                It.IsAny<Dictionary<string, object>>(),
                It.IsAny<CancellationToken>()),
            Times.Never);

        _apiClientMock.Verify(
            x => x.SetStateAsync(
                "sensor.nocturne_cob",
                It.IsAny<string>(),
                It.IsAny<Dictionary<string, object>>(),
                It.IsAny<CancellationToken>()),
            Times.Never);

        _apiClientMock.Verify(
            x => x.SetStateAsync(
                "sensor.nocturne_predicted_bg",
                It.IsAny<string>(),
                It.IsAny<Dictionary<string, object>>(),
                It.IsAny<CancellationToken>()),
            Times.Never);

        // Loop status still pushes "unknown" when no APS snapshot
        _apiClientMock.Verify(
            x => x.SetStateAsync(
                "sensor.nocturne_loop_status",
                "unknown",
                It.IsAny<Dictionary<string, object>>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task OnCreatedAsync_CachesApsSnapshotAcrossPushes()
    {
        SetupApsSnapshot(new ApsSnapshot
        {
            Iob = 2.0,
            Cob = 30.0,
            PredictedDefaultJson = "[120.0, 110.0]",
            Enacted = true,
            EnactedRate = 1.0,
            EnactedDuration = 30
        });

        _apiClientMock
            .Setup(x => x.SetStateAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<Dictionary<string, object>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var sink = CreateSink(writeBackTypes:
        [
            WriteBackDataType.Iob,
            WriteBackDataType.Cob,
            WriteBackDataType.PredictedBg,
            WriteBackDataType.LoopStatus
        ]);
        var entry = CreateRecentEntry();

        await sink.OnCreatedAsync(entry);

        // All 4 computed types pushed
        _apiClientMock.Verify(
            x => x.SetStateAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<Dictionary<string, object>>(), It.IsAny<CancellationToken>()),
            Times.Exactly(4));

        // But GetAsync called only once (cached)
        _apsSnapshotRepoMock.Verify(
            x => x.GetAsync(null, null, null, null, 1, 0, true, It.IsAny<CancellationToken>()),
            Times.Once);
    }
}

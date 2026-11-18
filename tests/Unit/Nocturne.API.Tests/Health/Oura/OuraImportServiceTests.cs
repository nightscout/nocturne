using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Health.Oura;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Oura.Export;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Sleep;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Health.Oura;

public class OuraImportServiceTests
{
    private const string SleepCsv =
        "id;bedtime_end;bedtime_start;day;total_sleep_duration;type\r\n" +
        "night-1;2026-03-02T06:00:00+00:00;2026-03-01T23:00:00+00:00;2026-03-02;23400;long_sleep\r\n" +
        "night-2;2026-03-03T06:00:00+00:00;2026-03-02T23:00:00+00:00;2026-03-03;23400;long_sleep\r\n";

    private const string HeartRateCsv =
        "bpm;source;timestamp\r\n61;awake;2026-03-01T10:00:00+00:00\r\n58;rest;2026-03-04T10:05:00+00:00\r\n";

    private const string ActivityCsv =
        "id;day;steps;timestamp\r\na1;2026-03-01;9123;2026-03-01T04:00:00+00:00\r\n";

    private readonly Mock<IHeartRateService> _heartRates = new();
    private readonly Mock<IStepCountService> _stepCounts = new();
    private readonly Mock<ISleepService> _sleep = new();
    private readonly Mock<ISleepSessionRepository> _sleepSessions = new();

    public OuraImportServiceTests()
    {
        _heartRates.Setup(s => s.CreateHeartRatesAsync(It.IsAny<IEnumerable<HeartRate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<HeartRate> batch, CancellationToken _) => batch);
        _stepCounts.Setup(s => s.CreateStepCountsAsync(It.IsAny<IEnumerable<StepCount>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<StepCount> batch, CancellationToken _) => batch);
    }

    private OuraImportService Service() => new(
        _heartRates.Object, _stepCounts.Object, _sleep.Object, _sleepSessions.Object,
        NullLogger<OuraImportService>.Instance);

    [Fact]
    public async Task AnExport_IsWrittenThroughTheHealthServices_AndReportedByCount()
    {
        var progress = new OuraImportProgress();
        await Service().ImportAsync(Export(), progress, CancellationToken.None);

        (progress.SleepSessionCount, progress.HeartRateCount, progress.StepDayCount).Should().Be((2, 2, 1));
        _sleep.Verify(s => s.UpsertSessionAsync(
            It.Is<SleepSession>(session => session.Source == SleepSource.Oura), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        _heartRates.Verify(s => s.CreateHeartRatesAsync(
            It.Is<IEnumerable<HeartRate>>(batch => batch.Count() == 2), It.IsAny<CancellationToken>()));
        _stepCounts.Verify(s => s.CreateStepCountsAsync(
            It.Is<IEnumerable<StepCount>>(batch => batch.Single().Metric == 9123), It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task ASessionTheUserDeleted_IsLeftDeletedAndNotCounted()
    {
        _sleep.Setup(s => s.UpsertSessionAsync(
                It.Is<SleepSession>(session => session.OriginalId == "oura:night-1"), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RecreationBlockedException("sleep session", "oura:night-1"));

        var progress = new OuraImportProgress();
        await Service().ImportAsync(Export(), progress, CancellationToken.None);

        progress.SleepSessionCount.Should().Be(1);
    }

    [Fact]
    public async Task TheCounts_AreOfTheRecordsStored_NotOfTheRowsRead()
    {
        _heartRates.Setup(s => s.CreateHeartRatesAsync(It.IsAny<IEnumerable<HeartRate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<HeartRate> batch, CancellationToken _) => batch.Take(1));

        var progress = new OuraImportProgress();
        await Service().ImportAsync(Export(), progress, CancellationToken.None);

        progress.HeartRateCount.Should().Be(1);
    }

    [Fact]
    public async Task AFileThatIsNotAnExport_WritesNothing()
    {
        using var notAZip = new MemoryStream(Encoding.UTF8.GetBytes("hello"));

        var import = () => Service().ImportAsync(notAZip, new OuraImportProgress(), CancellationToken.None);

        await import.Should().ThrowAsync<OuraExportException>();
        _sleep.VerifyNoOtherCalls();
        _heartRates.VerifyNoOtherCalls();
        _stepCounts.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AnExportPastTheSleepStageBudget_IsRefusedBeforeAnySessionIsWritten()
    {
        var progress = new OuraImportProgress();

        var import = () => Service().ImportAsync(OverStageBudget(), progress, CancellationToken.None);

        (await import.Should().ThrowAsync<OuraExportException>()).Which.Code.Should().Be(OuraExportException.TooMuchData);
        _sleep.VerifyNoOtherCalls();
        _heartRates.VerifyNoOtherCalls();
        progress.Total.Should().Be(0);
    }

    [Fact]
    public async Task ThePurge_DeletesTheSleepTheSourcePurgeCannotReach_AndReleasesItOnlyForAReimport()
    {
        var audit = Mock.Of<IAuditContext>();
        _sleepSessions.Setup(r => r.DeleteSourceAppAsync(
                SleepSource.Oura, "Oura", audit, "data_source=oura-connector", It.IsAny<CancellationToken>()))
            .ReturnsAsync(7);
        IConnectorDataPurge purge = Service();

        var kept = await purge.PurgeAsync(allowReimport: false, audit, CancellationToken.None);

        kept.Should().Equal(new Dictionary<string, long> { ["Sleep"] = 7 });
        _sleepSessions.Verify(r => r.AllowSourceAppReimportAsync(
            It.IsAny<SleepSource>(), It.IsAny<string>(), It.IsAny<IAuditContext>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);

        await purge.PurgeAsync(allowReimport: true, audit, CancellationToken.None);

        _sleepSessions.Verify(r => r.AllowSourceAppReimportAsync(
            SleepSource.Oura, "Oura", audit, "data_source=oura-connector", It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>Every period is within the budget, and together they pass it.</summary>
    internal static MemoryStream OverStageBudget()
    {
        var twoDays = string.Concat(Enumerable.Repeat("12", 2 * 24 * 60));
        var rows = Enumerable.Range(0, OuraExportArchive.MaxSleepStagesAndSamples / twoDays.Length + 1)
            .Select(i => $"p{i};2026-03-03T21:00:00+00:00;2026-03-01T21:00:00+00:00;2026-03-03;23400;long_sleep;{twoDays}");
        return Zip(("sleepmodel.csv",
            "id;bedtime_end;bedtime_start;day;total_sleep_duration;type;sleep_phase_30_sec\r\n" +
            string.Join("\r\n", rows) + "\r\n"));
    }

    internal static MemoryStream Export() =>
        Zip(("sleepmodel.csv", SleepCsv), ("heartrate.csv", HeartRateCsv), ("dailyactivity.csv", ActivityCsv));

    private static MemoryStream Zip(params (string Name, string Content)[] files)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }

        stream.Position = 0;
        return stream;
    }
}

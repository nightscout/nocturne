using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Services.Alerts;
using Nocturne.Tests.Shared.Logging;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models.Alerts;
using Npgsql;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// A report of a clock anomaly runs after the evaluation it describes, so nothing it throws, a defect
/// included, may fail that evaluation. Every failure is logged at error.
/// </summary>
[Trait("Category", "Unit")]
public class ClockAnomalyNoticeIsolationTests
{
    private static readonly DateTime Now = new(2026, 11, 27, 3, 0, 0, DateTimeKind.Utc);

    private static readonly SensorContext Context = new()
    {
        LatestValue = null,
        LatestTimestamp = null,
        TrendRate = null,
        LastReadingAt = null,
    };

    public static TheoryData<Exception> Failures => new()
    {
        new NpgsqlException("connection refused"),
        new InvalidOperationException("no tenant"),
        new NullReferenceException(),
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task A_failed_pass_report_is_logged_at_error_and_does_not_stop_the_evaluation(Exception failure)
    {
        var notice = new Mock<IClockAnomalyNotice>();
        notice
            .Setup(n => n.ReportAsync(It.IsAny<SensorContext>(), It.IsAny<IReadOnlyCollection<ClockAnomaly>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);
        var logger = new ListLogger<ClockAnomalyNoticeIsolationTests>();

        await notice.Object.ReportIsolatedAsync(Context, [], logger, CancellationToken.None);

        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error && e.Exception == failure);
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task A_failed_reading_report_is_logged_at_error_and_does_not_stop_the_evaluation(Exception failure)
    {
        var notice = new Mock<IClockAnomalyNotice>();
        notice
            .Setup(n => n.ReportReadingAsync(It.IsAny<AlertReadingSelection>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);
        var logger = new ListLogger<ClockAnomalyNoticeIsolationTests>();

        await notice.Object.ReportReadingIsolatedAsync(new AlertReadingSelection(null, null), Now, logger, CancellationToken.None);

        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error && e.Exception == failure);
    }

    [Fact]
    public async Task The_callers_cancellation_is_not_logged_and_reaches_the_caller()
    {
        using var shutdown = new CancellationTokenSource();
        await shutdown.CancelAsync();
        var notice = new Mock<IClockAnomalyNotice>();
        notice
            .Setup(n => n.ReportAsync(It.IsAny<SensorContext>(), It.IsAny<IReadOnlyCollection<ClockAnomaly>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(shutdown.Token));
        var logger = new ListLogger<ClockAnomalyNoticeIsolationTests>();

        var report = () => notice.Object.ReportIsolatedAsync(Context, [], logger, shutdown.Token);

        await report.Should().ThrowAsync<OperationCanceledException>();
        logger.Entries.Should().BeEmpty();
    }
}

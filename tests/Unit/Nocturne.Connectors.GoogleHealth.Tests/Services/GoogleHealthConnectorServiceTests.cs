using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.GoogleHealth.Configurations;
using Nocturne.Connectors.GoogleHealth.Services;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Health;
using Xunit;

namespace Nocturne.Connectors.GoogleHealth.Tests.Services;

public class GoogleHealthConnectorServiceTests
{
    [Fact]
    public async Task Page_progress_is_coalesced_but_phase_transitions_include_the_final_count()
    {
        var clock = new TestTimeProvider();
        var pages = 0;
        var fixture = new Fixture(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/token")
                return Json($$"""{"access_token":"access","expires_in":3600,"scope":"{{GoogleHealthClient.MetricsScope}}"}""");
            clock.Advance(TimeSpan.FromMilliseconds(250));
            pages++;
            return Json(pages < 10 ? $$"""{"dataPoints":[],"nextPageToken":"{{pages}}"}""" : "{\"dataPoints\":[]}");
        }, clock);
        var from = DateTime.UtcNow.AddDays(-1);
        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { From = from, To = from.AddHours(1) }, fixture.Configuration(), default);

        Assert.True(result.Success);
        Assert.Equal(10, pages);
        fixture.Coordinator.Verify(value => value.ReportAsync(It.IsAny<Guid>(), GoogleHealthSyncPhase.Reading,
            "weight", 0, 1, It.Is<int?>(count => count > 0)), Times.Once);
        fixture.Coordinator.Verify(value => value.ReportAsync(It.IsAny<Guid>(), GoogleHealthSyncPhase.Reading,
            "weight", 0, 1, 8), Times.Once);
        fixture.Coordinator.Verify(value => value.ReportAsync(It.IsAny<Guid>(), GoogleHealthSyncPhase.Integrating,
            "weight", 0, 1, 10), Times.Once);
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public void Advance(TimeSpan interval) => ticks += interval.Ticks;
    }

    [Theory]
    [InlineData("weight")]
    [InlineData("sleep")]
    public async Task A_malformed_later_page_abandons_reconciliation_and_preserves_the_cursor(string type)
    {
        var calls = 0;
        var sample = DateTimeOffset.UtcNow.AddMinutes(-10);
        var valid = type == "weight"
            ? """{"name":"retained","weight":{"sampleTime":{"physicalTime":"TIME"},"weightGrams":72500}}""".Replace("TIME", sample.ToString("O"))
            : """{"name":"retained","sleep":{"interval":{"startTime":"START","endTime":"END"}}}"""
                .Replace("START", sample.AddHours(-8).ToString("O")).Replace("END", sample.ToString("O"));
        var fixture = new Fixture(request => request.RequestUri!.AbsolutePath == "/token"
            ? Json($$"""{"access_token":"access","expires_in":3600,"token_type":"Bearer","scope":"{{GoogleHealthClient.ScopeFor(type)}}"}""")
            : Json(++calls == 1 ? "{\"dataPoints\":[" + valid + "],\"nextPageToken\":\"next\"}"
                : "{\"dataPoints\":[" + valid + ",{\"name\":\"malformed\"}]}"));
        var config = fixture.Configuration();
        config.SyncBodyWeight = type == "weight";
        config.SyncSleep = type == "sleep";
        fixture.SetSession("refresh", GoogleHealthClient.ScopeFor(type));
        var result = await fixture.Service.SyncDataAsync(new SyncRequest { From = sample.AddHours(-23).UtcDateTime, To = sample.AddHours(1).UtcDateTime }, config, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("invalid_google_data", result.Message);
        fixture.Writer.Verify(writer => writer.CompleteReconciliationAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.Writer.Verify(writer => writer.AbandonReconciliationAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Null(fixture.LastSavedConfiguration);
    }

    [Fact]
    public async Task Sync_requires_a_durable_oauth_session()
    {
        var fixture = new Fixture(_ => Json("{}"));
        var config = fixture.Configuration();
        config.RefreshToken = null;
        fixture.SetSession(null, GoogleHealthClient.MetricsScope);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest(), config, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("reconnect_required", result.Message);
        fixture.Writer.Verify(value => value.WriteAsync(
            It.IsAny<IReadOnlyCollection<GoogleHealthReading>>(),
            It.IsAny<IReadOnlyCollection<Nocturne.Core.Models.SleepSession>>(),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Sync_fetches_selected_data_and_publishes_to_native_health_services()
    {
        var sampleTime = DateTimeOffset.UtcNow.AddMinutes(-10);
        var fixture = new Fixture(request => request.RequestUri!.AbsolutePath switch
        {
            "/token" => Json($$"""{"access_token":"access","refresh_token":"rotated","expires_in":3600,"token_type":"Bearer","scope":"{{GoogleHealthClient.MetricsScope}}"}"""),
            var path when path.Contains("/weight/") => Json(
                """{"dataPoints":[{"weight":{"sampleTime":{"physicalTime":"TIME"},"weightGrams":72500}}]}"""
                    .Replace("TIME", sampleTime.ToString("O"))),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
        });
        var config = fixture.Configuration();
        config.HistoryDays = 30;

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest(), config, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, result.ItemsSynced[SyncDataType.BodyWeight]);
        fixture.Writer.Verify(value => value.WriteAsync(
            It.Is<IReadOnlyCollection<GoogleHealthReading>>(items =>
                items.Count == 1 && items.Single().Value == 72.5m),
            It.Is<IReadOnlyCollection<Nocturne.Core.Models.SleepSession>>(items => items.Count == 0),
            2,
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal("rotated", fixture.Secrets["refreshToken"]);
    }

    [Fact]
    public async Task Successful_sync_persists_its_own_resume_watermark()
    {
        var sampleTime = DateTimeOffset.UtcNow.AddMinutes(-10);
        var fixture = new Fixture(request => request.RequestUri!.AbsolutePath switch
        {
            "/token" => Json($$"""{"access_token":"access","refresh_token":"rotated","expires_in":3600,"token_type":"Bearer","scope":"{{GoogleHealthClient.MetricsScope}}"}"""),
            var path when path.Contains("/weight/") => Json(
                """{"dataPoints":[{"weight":{"sampleTime":{"physicalTime":"TIME"},"weightGrams":72500}}]}"""
                    .Replace("TIME", sampleTime.ToString("O"))),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
        });
        var config = fixture.Configuration();
        config.HistoryDays = 30;

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest(), config, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("lastSyncedTo", fixture.LastSavedConfiguration);
        Assert.Equal(0, fixture.ConfigurationSaves);

        var repeated = await fixture.Service.SyncDataAsync(
            new SyncRequest(), config, CancellationToken.None);

        Assert.True(repeated.Success);
    }

    [Theory]
    [InlineData("2026-09-10T10:00:00Z")]
    [InlineData("2026-09-10T12:00:00+02:00")]
    [InlineData("2026-09-10T10:00:00")]
    [InlineData("2026-09-10T10:00:37.123Z")]
    [InlineData("2026-09-10T12:00:37.123+02:00")]
    public async Task Scheduled_sync_resumes_from_a_persisted_watermark_in_utc(string watermark)
    {
        var requestedFrom = new List<DateTimeOffset>();
        var fixture = new Fixture(request => request.RequestUri!.AbsolutePath switch
        {
            "/token" => Json($$"""{"access_token":"access","refresh_token":"refresh","expires_in":3600,"token_type":"Bearer","scope":"{{GoogleHealthClient.MetricsScope}}"}"""),
            var path when path.Contains("/weight/") => CaptureRange(request, requestedFrom.Add),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
        });
        fixture.StoredConfiguration = JsonSerializer.Serialize(new { importFrom = (string?)null, lastSyncedTo = watermark });

        var result = await fixture.Service.SyncDataAsync(fixture.Configuration(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(new DateTimeOffset(2026, 9, 10, 9, 55, 0, TimeSpan.Zero), requestedFrom[0]);
        var today = DateTimeOffset.UtcNow.Date;
        var monthStart = new DateTime(today.AddDays(-1).Year, today.AddDays(-1).Month, 1);
        Assert.Equal(today.AddDays(-7) > monthStart ? today.AddDays(-7) : monthStart, requestedFrom[1].UtcDateTime.Date);
    }

    [Theory]
    [InlineData("2026-09-10T10:00:00Z")]
    [InlineData("2026-09-10T12:00:00+02:00")]
    [InlineData("2026-09-10T10:00:00")]
    public async Task Older_manual_backfill_does_not_move_the_watermark_backwards(string watermark)
    {
        var fixture = new Fixture(request => request.RequestUri!.AbsolutePath switch
        {
            "/token" => Json($$"""{"access_token":"access","refresh_token":"refresh","expires_in":3600,"token_type":"Bearer","scope":"{{GoogleHealthClient.MetricsScope}}"}"""),
            var path when path.Contains("/weight/") => Json("{\"dataPoints\":[]}"),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
        });
        fixture.StoredConfiguration = JsonSerializer.Serialize(new { importFrom = (string?)null, lastSyncedTo = watermark });
        var result = await fixture.Service.SyncDataAsync(new SyncRequest
        {
            From = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            To = new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc)
        }, fixture.Configuration(), CancellationToken.None);

        Assert.True(result.Success);
        using var stored = JsonDocument.Parse(fixture.StoredConfiguration);
        Assert.Equal(watermark, stored.RootElement.GetProperty("lastSyncedTo").GetString());
        Assert.Null(fixture.LastSavedConfiguration);
    }

    [Fact]
    public async Task Partial_consent_does_not_advance_the_shared_watermark_or_consume_history()
    {
        var fixture = new Fixture(request => request.RequestUri!.AbsolutePath switch
        {
            "/token" => Json($$"""{"access_token":"access","refresh_token":"refresh","expires_in":3600,"token_type":"Bearer","scope":"{{GoogleHealthClient.MetricsScope}}"}"""),
            var path when path.Contains("/weight/") => Json("{\"dataPoints\":[]}"),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
        });
        var config = fixture.Configuration();
        config.SyncSteps = true;
        config.ImportFrom = "2026-09-01T00:00:00.0000000+00:00";

        var result = await fixture.Service.SyncDataAsync(new SyncRequest(), config, CancellationToken.None);

        Assert.True(result.Success);
        Assert.StartsWith("partial_consent", result.Message);
        Assert.False(fixture.ImportFromWasConsumed);
        using var stored = JsonDocument.Parse(fixture.StoredConfiguration);
        Assert.True(stored.RootElement.TryGetProperty("importFrom", out var importFrom));
        Assert.Equal(JsonValueKind.String, importFrom.ValueKind);
        Assert.False(stored.RootElement.TryGetProperty("lastSyncedTo", out _));
    }

    [Fact]
    public async Task Requested_data_types_narrow_the_configured_selection()
    {
        var fixture = new Fixture(request => request.RequestUri!.AbsolutePath switch
        {
            "/token" => Json($$"""{"access_token":"access","refresh_token":"refresh","expires_in":3600,"token_type":"Bearer","scope":"{{GoogleHealthClient.MetricsScope}} {{GoogleHealthClient.ActivityScope}}"}"""),
            var path when path.Contains("/weight/") => Json("{\"dataPoints\":[]}"),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
        });
        var config = fixture.Configuration();
        config.SyncSteps = true;

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.BodyWeight] },
            config,
            CancellationToken.None);

        Assert.True(result.Success);
        fixture.Writer.Verify(value => value.WriteAsync(
            It.IsAny<IReadOnlyCollection<GoogleHealthReading>>(),
            It.IsAny<IReadOnlyCollection<Nocturne.Core.Models.SleepSession>>(),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Scheduled_sync_refreshes_today_then_backfills_one_calendar_month()
    {
        var requestedFrom = new List<DateTimeOffset>();
        var fixture = new Fixture(request => request.RequestUri!.AbsolutePath switch
        {
            "/token" => Json($$"""{"access_token":"access","refresh_token":"refresh","expires_in":3600,"token_type":"Bearer","scope":"{{GoogleHealthClient.MetricsScope}}"}"""),
            var path when path.Contains("/weight/") => CaptureRange(request, requestedFrom.Add),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
        });
        var config = fixture.Configuration();
        config.ImportFrom = "2000-01-01T00:00:00.0000000+00:00";
        config.HistoryDays = 7;

        var result = await fixture.Service.SyncDataAsync(config, CancellationToken.None);

        Assert.True(result.Success);
        var today = DateTimeOffset.UtcNow.Date;
        Assert.Equal(today, requestedFrom[0].UtcDateTime.Date);
        var firstMonth = new DateTime(today.AddDays(-1).Year, today.AddDays(-1).Month, 1);
        Assert.Equal(firstMonth, requestedFrom[1].UtcDateTime.Date);

        var repeated = await fixture.Service.SyncDataAsync(config, CancellationToken.None);

        Assert.True(repeated.Success);
        Assert.InRange(requestedFrom[2], DateTimeOffset.UtcNow.AddMinutes(-6), DateTimeOffset.UtcNow);
        Assert.Equal(firstMonth.AddMonths(-1), requestedFrom[3].UtcDateTime.Date);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Failed_initial_import_does_not_claim_backfill_progress(bool failHistorical, bool noHistory)
    {
        var reads = 0;
        var fixture = new Fixture(request => request.RequestUri!.AbsolutePath == "/token"
            ? Json($$"""{"access_token":"access","refresh_token":"refresh","expires_in":3600,"scope":"{{GoogleHealthClient.MetricsScope}}"}""")
            : ++reads == (failHistorical ? 2 : 1)
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : Json("""{"dataPoints":[]}"""));
        var config = fixture.Configuration();
        if (noHistory) config.ImportFrom = DateTimeOffset.UtcNow.Date.ToString("O");

        var result = await fixture.Service.SyncDataAsync(new SyncRequest(), config, default);

        Assert.False(result.Success);
        using var stored = JsonDocument.Parse(fixture.StoredConfiguration);
        Assert.False(stored.RootElement.TryGetProperty("backfillCursorDate", out var cursor) && cursor.ValueKind == JsonValueKind.String);
        Assert.False(stored.RootElement.TryGetProperty("backfillComplete", out var complete) && complete.ValueKind == JsonValueKind.True);
    }

    [Fact]
    public async Task Failed_historical_window_is_halved_for_the_next_attempt()
    {
        var weightRequests = 0;
        var retryRanges = new List<DateTimeOffset>();
        var failHistoricalWindow = true;
        var fixture = new Fixture(request => request.RequestUri!.AbsolutePath switch
        {
            "/token" => Json($$"""{"access_token":"access","refresh_token":"refresh","expires_in":3600,"token_type":"Bearer","scope":"{{GoogleHealthClient.MetricsScope}}"}"""),
            var path when path.Contains("/weight/") && failHistoricalWindow && ++weightRequests == 2 =>
                new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            var path when path.Contains("/weight/") => CaptureRange(request, retryRanges.Add),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
        });
        var config = fixture.Configuration();
        config.HistoryDays = 7;

        var today = DateTimeOffset.UtcNow.Date;
        var monthStart = new DateTime(today.AddDays(-1).Year, today.AddDays(-1).Month, 1);
        var windowFrom = today.AddDays(-7) > monthStart ? today.AddDays(-7) : monthStart;
        var retryDays = Math.Max(1, (int)Math.Ceiling((today - windowFrom).TotalDays / 2));

        var failed = await fixture.Service.SyncDataAsync(config, CancellationToken.None);

        Assert.False(failed.Success);
        using (var stored = JsonDocument.Parse(fixture.StoredConfiguration))
        {
            Assert.Equal(retryDays, stored.RootElement.GetProperty("backfillChunkDays").GetInt32());
            Assert.True(stored.RootElement.TryGetProperty("lastSyncedTo", out _));
        }

        failHistoricalWindow = false;
        retryRanges.Clear();
        var retried = await fixture.Service.SyncDataAsync(config, CancellationToken.None);

        Assert.True(retried.Success);
        Assert.InRange(retryRanges[0], DateTimeOffset.UtcNow.AddMinutes(-6), DateTimeOffset.UtcNow);
        Assert.Equal(today.AddDays(-retryDays), retryRanges[1].UtcDateTime.Date);
    }

    [Fact]
    public async Task Sync_writes_each_page_before_reconciling_the_completed_type()
    {
        var calls = 0;
        var fixture = new Fixture(request => request.RequestUri!.AbsolutePath switch
        {
            "/token" => Json($$"""{"access_token":"access","refresh_token":"refresh","expires_in":3600,"token_type":"Bearer","scope":"{{GoogleHealthClient.MetricsScope}}"}"""),
            var path when path.Contains("/weight/") => Json(++calls == 1
                ? """{"dataPoints":[{"name":"first","weight":{"sampleTime":{"physicalTime":"2026-09-01T10:00:00Z"},"weightGrams":70000}}],"nextPageToken":"next"}"""
                : """{"dataPoints":[{"name":"second","weight":{"sampleTime":{"physicalTime":"2026-09-01T11:00:00Z"},"weightGrams":71000}}]}"""),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
        });
        var config = fixture.Configuration();
        config.HistoryDays = 30;
        var from = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { From = from, To = from.AddDays(1) }, config, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, result.ItemsSynced[SyncDataType.BodyWeight]);
        Assert.Equal([
            "BeginReconciliationAsync", "StageReconciliationIdsAsync", "WriteAsync",
            "StageReconciliationIdsAsync", "WriteAsync", "CompleteReconciliationAsync"
        ],
            fixture.Writer.Invocations.Select(invocation => invocation.Method.Name));
    }

    [Fact]
    public async Task Sync_does_not_reconcile_when_a_later_page_fails()
    {
        var calls = 0;
        var fixture = new Fixture(request => request.RequestUri!.AbsolutePath switch
        {
            "/token" => Json($$"""{"access_token":"access","refresh_token":"refresh","expires_in":3600,"token_type":"Bearer","scope":"{{GoogleHealthClient.MetricsScope}}"}"""),
            var path when path.Contains("/weight/") => ++calls == 1
                ? Json("""{"dataPoints":[{"name":"first","weight":{"sampleTime":{"physicalTime":"2026-09-01T10:00:00Z"},"weightGrams":70000}}],"nextPageToken":"next"}""")
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
        });
        var config = fixture.Configuration();
        config.HistoryDays = 30;
        var from = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { From = from, To = from.AddDays(1) }, config, CancellationToken.None);

        Assert.False(result.Success);
        fixture.Writer.Verify(value => value.WriteAsync(
            It.Is<IReadOnlyCollection<GoogleHealthReading>>(readings => readings.Count == 1),
            It.IsAny<IReadOnlyCollection<Nocturne.Core.Models.SleepSession>>(),
            It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        fixture.Writer.Verify(value => value.CompleteReconciliationAsync(
            It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.Writer.Verify(value => value.AbandonReconciliationAsync(
            It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Null(fixture.LastSavedConfiguration);
    }

    [Fact]
    public async Task Native_storage_failure_keeps_stage_and_data_type_in_result()
    {
        var fixture = new Fixture(request => request.RequestUri!.AbsolutePath switch
        {
            "/token" => Json($$"""{"access_token":"access","refresh_token":"refresh","expires_in":3600,"token_type":"Bearer","scope":"{{GoogleHealthClient.MetricsScope}}"}"""),
            var path when path.Contains("/weight/") => Json(
                """{"dataPoints":[{"name":"weight-1","weight":{"sampleTime":{"physicalTime":"2026-09-01T10:00:00Z"},"weightGrams":70000}}]}"""),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
        });
        fixture.Writer
            .Setup(value => value.WriteAsync(
                It.IsAny<IReadOnlyCollection<GoogleHealthReading>>(),
                It.IsAny<IReadOnlyCollection<Nocturne.Core.Models.SleepSession>>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database unavailable"));

        var from = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { From = from, To = from.AddDays(1) },
            fixture.Configuration(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("internal_sync_native_write:weight", result.Message);
        Assert.Contains(result.Errors, value => value == result.Message);
        fixture.Writer.Verify(value => value.AbandonReconciliationAsync(
            It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Manual_backfill_consumes_the_import_start_date_once_backfill_reaches_the_floor()
    {
        var fixture = new Fixture(request => request.RequestUri!.AbsolutePath switch
        {
            "/token" => Json($$"""{"access_token":"access","refresh_token":"refresh","expires_in":3600,"token_type":"Bearer","scope":"{{GoogleHealthClient.MetricsScope}}"}"""),
            var path when path.Contains("/weight/") => Json("{\"dataPoints\":[]}"),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
        });
        var config = fixture.Configuration();
        // A floor three days back is reached by the first partial calendar-month window.
        config.ImportFrom = DateTimeOffset.UtcNow.AddDays(-3).ToString("O");

        Assert.False(fixture.ImportFromWasConsumed);
        for (var attempt = 0; attempt < 5 && !fixture.ImportFromWasConsumed; attempt++)
        {
            var result = await fixture.Service.SyncDataAsync(new SyncRequest(), config, CancellationToken.None);
            Assert.True(result.Success);
        }

        Assert.True(fixture.ImportFromWasConsumed);
    }

    [Fact]
    public async Task Heart_rate_limit_stops_pagination_before_accumulating_or_writing_more_samples()
    {
        var pagesRead = 0;
        var fixture = new Fixture(request => request.RequestUri!.AbsolutePath switch
        {
            "/token" => Json($$"""{"access_token":"access","refresh_token":"refresh","expires_in":3600,"scope":"{{GoogleHealthClient.MetricsScope}}"}"""),
            _ => HeartRatePage()
        });
        HttpResponseMessage HeartRatePage()
        {
            pagesRead++;
            return Json("""
                {"dataPoints":[
                    {"name":"a","heartRate":{"sampleTime":{"physicalTime":"2026-09-01T10:00:05Z"},"beatsPerMinute":"60"}},
                    {"name":"b","heartRate":{"sampleTime":{"physicalTime":"2026-09-01T10:00:45Z"},"beatsPerMinute":"70"}}
                ],"nextPageToken":"more"}
                """);
        }
        fixture.Writer.Setup(value => value.StageReconciliationIdsAsync(
                It.IsAny<Guid>(), "heart-rate", It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GoogleHealthException("history_too_large",
                stage: "native_reconciliation_stage", dataType: "heart-rate"));
        var config = fixture.Configuration();
        config.SyncBodyWeight = false;
        config.SyncHeartRate = true;
        var from = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { From = from, To = from.AddDays(1) }, config, default);

        Assert.False(result.Success);
        Assert.Equal("history_too_large:heart-rate", result.Message);
        Assert.Equal(1, pagesRead);
        fixture.Writer.Verify(value => value.StageReconciliationIdsAsync(
            It.IsAny<Guid>(), "heart-rate",
            It.Is<IReadOnlyCollection<string>>(ids => ids.Count == 1),
            It.IsAny<CancellationToken>()), Times.Once);
        fixture.Writer.Verify(value => value.WriteAsync(
            It.IsAny<IReadOnlyCollection<GoogleHealthReading>>(),
            It.IsAny<IReadOnlyCollection<Nocturne.Core.Models.SleepSession>>(),
            It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.Writer.Verify(value => value.CompleteReconciliationAsync(
            It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.Writer.Verify(value => value.AbandonReconciliationAsync(
            It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.DoesNotContain("lastSyncedTo", fixture.StoredConfiguration);
    }

    [Fact]
    public async Task Live_overlap_recomputes_the_complete_first_heart_rate_minute()
    {
        var requestedFrom = new List<DateTimeOffset>();
        var fixture = new Fixture(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/token")
                return Json($$"""{"access_token":"access","refresh_token":"refresh","expires_in":3600,"scope":"{{GoogleHealthClient.MetricsScope}}"}""");
            CaptureRange(request, requestedFrom.Add).Dispose();
            var firstSample = requestedFrom[^1] <= new DateTimeOffset(2026, 9, 1, 10, 0, 5, TimeSpan.Zero)
                ? """{"name":"a","heartRate":{"sampleTime":{"physicalTime":"2026-09-01T10:00:05Z"},"beatsPerMinute":"60"}},"""
                : "";
            return Json("{\"dataPoints\":[" + firstSample + """
                {"name":"b","heartRate":{"sampleTime":{"physicalTime":"2026-09-01T10:00:45Z"},"beatsPerMinute":"70"}}]}
                """);
        });
        fixture.StoredConfiguration = """{"lastSyncedTo":"2026-09-01T10:05:37.123Z","backfillComplete":true,"backfillCursorDate":"2026-09-01T00:00:00Z","backfillFloorDate":"2026-09-01T00:00:00Z"}""";
        var config = fixture.Configuration();
        config.SyncBodyWeight = false;
        config.SyncHeartRate = true;

        var result = await fixture.Service.SyncDataAsync(new SyncRequest(), config, default);

        Assert.True(result.Success);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero), requestedFrom[0]);
        fixture.Writer.Verify(value => value.WriteAsync(
            It.Is<IReadOnlyCollection<GoogleHealthReading>>(items => items.Count == 1 && items.Single().Value == 65m),
            It.IsAny<IReadOnlyCollection<Nocturne.Core.Models.SleepSession>>(),
            It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Heart_rate_samples_are_aggregated_to_one_average_per_utc_minute()
    {
        var fixture = new Fixture(request => request.RequestUri!.AbsolutePath switch
        {
            "/token" => Json($$"""{"access_token":"access","refresh_token":"refresh","expires_in":3600,"token_type":"Bearer","scope":"{{GoogleHealthClient.MetricsScope}}"}"""),
            var path when path.Contains("/heart-rate/") => Json("""
                {"dataPoints":[
                    {"name":"a","heartRate":{"sampleTime":{"physicalTime":"2026-09-01T10:00:05Z"},"beatsPerMinute":"60"}},
                    {"name":"b","heartRate":{"sampleTime":{"physicalTime":"2026-09-01T10:00:45Z"},"beatsPerMinute":"70"}},
                    {"name":"c","heartRate":{"sampleTime":{"physicalTime":"2026-09-01T10:01:10Z"},"beatsPerMinute":"80"}}
                ]}
                """),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
        });
        var config = fixture.Configuration();
        config.SyncBodyWeight = false;
        config.SyncHeartRate = true;
        var from = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var firstMinute = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var secondMinute = new DateTimeOffset(2026, 9, 1, 10, 1, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { From = from, To = from.AddDays(1) }, config, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, result.ItemsSynced[SyncDataType.HeartRate]);
        fixture.Writer.Verify(value => value.WriteAsync(
            It.Is<IReadOnlyCollection<GoogleHealthReading>>(items =>
                items.Count == 2 &&
                items.Any(item => item.Mills == firstMinute && item.Value == 65m) &&
                items.Any(item => item.Mills == secondMinute && item.Value == 80m)),
            It.IsAny<IReadOnlyCollection<Nocturne.Core.Models.SleepSession>>(),
            2, It.IsAny<CancellationToken>()), Times.Once);

        // Re-importing the same day updates the same two minute buckets rather than accumulating
        // more rows, because their SyncIdentifier is derived from the minute, not the raw sample.
        var repeated = await fixture.Service.SyncDataAsync(
            new SyncRequest { From = from, To = from.AddDays(1) }, config, CancellationToken.None);

        Assert.True(repeated.Success);
        Assert.Equal(2, repeated.ItemsSynced[SyncDataType.HeartRate]);
    }

    [Fact]
    public async Task Sleep_import_preserves_overnight_stages_and_completes_repeated_runs()
    {
        var fixture = new Fixture(request => request.RequestUri!.AbsolutePath switch
        {
            "/token" => Json($$"""{"access_token":"access","refresh_token":"refresh","expires_in":3600,"token_type":"Bearer","scope":"{{GoogleHealthClient.SleepScope}}"}"""),
            var path when path.Contains("/sleep/") => Json("""
                {"dataPoints":[{"name":"night-1","sleep":{
                  "interval":{"startTime":"2026-09-04T22:00:00Z","endTime":"2026-09-05T06:00:00Z"},
                  "stages":[
                    {"startTime":"2026-09-04T22:00:00Z","endTime":"2026-09-05T02:00:00Z","type":"DEEP"},
                    {"startTime":"2026-09-05T02:00:00Z","endTime":"2026-09-05T06:00:00Z","type":"REM"}
                  ]}}]}
                """),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
        });
        var config = fixture.Configuration();
        config.SyncBodyWeight = false;
        config.SyncSleep = true;
        config.GrantedScopes = GoogleHealthClient.SleepScope;
        fixture.SetSession("refresh", GoogleHealthClient.SleepScope);
        var from = new DateTime(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc);
        var request = new SyncRequest { From = from, To = from.AddDays(1) };

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var result = await fixture.Service.SyncDataAsync(request, config, default);
            Assert.True(result.Success);
            Assert.Equal(1, result.ItemsSynced[SyncDataType.Sleep]);
        }

        fixture.Writer.Verify(value => value.WriteAsync(
            It.Is<IReadOnlyCollection<GoogleHealthReading>>(items => items.Count == 0),
            It.Is<IReadOnlyCollection<Nocturne.Core.Models.SleepSession>>(items =>
                items.Count == 1 && items.Single().StartTime == from.AddHours(-2) &&
                items.Single().EndTime == from.AddHours(6) && items.Single().Stages!.Count == 2),
            2, It.IsAny<CancellationToken>()), Times.Exactly(2));
        fixture.Writer.Verify(value => value.StageReconciliationIdsAsync(
            It.IsAny<Guid>(), "sleep",
            It.Is<IReadOnlyCollection<string>>(identifiers => identifiers.Count == 1),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
        fixture.Writer.Verify(value => value.CompleteReconciliationAsync(
            It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        using var saved = JsonDocument.Parse(fixture.StoredConfiguration);
        Assert.Equal(from.AddDays(1), saved.RootElement.GetProperty("lastSyncedTo").GetDateTime());
    }

    [Fact]
    public async Task Waiting_sync_reloads_rotated_tokens_after_acquiring_the_gate()
    {
        string? requestedRefresh = null;
        var fixture = new Fixture(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/token")
            {
                requestedRefresh = System.Web.HttpUtility.ParseQueryString(
                    request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())["refresh_token"];
                return Json($$"""{"access_token":"access","refresh_token":"rotated-again","expires_in":3600,"scope":"{{GoogleHealthClient.MetricsScope}}"}""");
            }
            return Json("{\"dataPoints\":[]}");
        });
        var staleConfig = fixture.Configuration();
        await fixture.Gate.WaitAsync();
        var pending = fixture.Service.SyncDataAsync(new SyncRequest(), staleConfig, default);
        Assert.False(pending.IsCompleted);
        fixture.SetSession("newest-refresh", GoogleHealthClient.MetricsScope);
        fixture.Gate.Release();

        var result = await pending;

        Assert.True(result.Success);
        Assert.Equal("newest-refresh", requestedRefresh);
        Assert.Equal("rotated-again", fixture.Secrets["refreshToken"]);
    }

    [Fact]
    public async Task Waiting_sync_does_not_restore_a_disconnected_session()
    {
        var fixture = new Fixture(_ => throw new InvalidOperationException("Google must not be called"));
        var config = fixture.Configuration();
        await fixture.Gate.WaitAsync();
        var pending = fixture.Service.SyncDataAsync(new SyncRequest(), config, default);
        fixture.SetSession(null, "");
        fixture.Gate.Release();

        var result = await pending;

        Assert.False(result.Success);
        Assert.Equal("reconnect_required", result.Message);
        Assert.False(fixture.Secrets.ContainsKey("refreshToken"));
    }

    [Fact]
    public async Task Native_reconciliation_limit_splits_windows_and_completes_backfill()
    {
        var fixture = new Fixture(request => request.RequestUri!.AbsolutePath switch
        {
            "/token" => Json($$"""{"access_token":"access","refresh_token":"refresh","expires_in":3600,"scope":"{{GoogleHealthClient.MetricsScope}}"}"""),
            _ => Json("""{"dataPoints":[]}""")
        });
        var windows = new List<(DateTimeOffset From, DateTimeOffset To)>();
        fixture.Writer.Setup(value => value.BeginReconciliationAsync(
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<DateTimeOffset>(),
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns<IReadOnlyCollection<string>, DateTimeOffset, DateTimeOffset, CancellationToken>(
                (_, from, to, _) =>
                {
                    windows.Add((from, to));
                    return to - from > TimeSpan.FromDays(1)
                        ? Task.FromException<Guid>(new GoogleHealthException(
                            "history_too_large", stage: "native_reconciliation_stage", dataType: "weight"))
                        : Task.FromResult(Guid.NewGuid());
                });
        var config = fixture.Configuration();
        config.ImportFrom = DateTimeOffset.UtcNow.AddDays(-3).ToString("O");

        var result = await fixture.Service.SyncDataAsync(new SyncRequest(), config, default);

        Assert.True(result.Success);
        for (var attempt = 0; attempt < 3 && !fixture.ImportFromWasConsumed; attempt++)
            Assert.True((await fixture.Service.SyncDataAsync(new SyncRequest(), config, default)).Success);
        Assert.True(fixture.ImportFromWasConsumed);
        Assert.Contains(windows, window => window.To - window.From > TimeSpan.FromDays(1));
        Assert.True(windows.Count(window => window.To - window.From <= TimeSpan.FromDays(1)) >= 4);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Explicit_range_completes_the_full_requested_history(bool hasLowerBound)
    {
        var fixture = new Fixture(request => request.RequestUri!.AbsolutePath switch
        {
            "/token" => Json($$"""{"access_token":"access","refresh_token":"refresh","expires_in":3600,"scope":"{{GoogleHealthClient.MetricsScope}}"}"""),
            _ => Json("""{"dataPoints":[]}""")
        });
        var from = new DateTime(2025, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2025, 5, 10, 18, 0, 0, DateTimeKind.Utc);
        var windows = new List<(DateTimeOffset From, DateTimeOffset To)>();
        fixture.Writer.Setup(value => value.BeginReconciliationAsync(
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<DateTimeOffset>(),
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns<IReadOnlyCollection<string>, DateTimeOffset, DateTimeOffset, CancellationToken>(
                (_, start, end, _) =>
                {
                    windows.Add((start, end));
                    return Task.FromResult(Guid.NewGuid());
                });

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { From = hasLowerBound ? from : null, To = to }, fixture.Configuration(), default);

        Assert.True(result.Success);
        var window = Assert.Single(windows);
        Assert.Equal(hasLowerBound ? new DateTimeOffset(from) : new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero), window.From);
        Assert.Equal(new DateTimeOffset(to), window.To);
        Assert.False(fixture.ImportFromWasConsumed);
    }

    private sealed class Fixture
    {
        private readonly Guid tenantId = Guid.NewGuid();
        private GoogleHealthConnectorConfiguration? currentConfiguration;
        private Dictionary<string, string> secrets = new(StringComparer.OrdinalIgnoreCase)
        {
            ["refreshToken"] = "refresh",
            ["grantedScopes"] = GoogleHealthClient.MetricsScope
        };

        public Fixture(Func<HttpRequestMessage, HttpResponseMessage> responder, TimeProvider? timeProvider = null)
        {
            var tenant = new Mock<ITenantAccessor>();
            tenant.SetupGet(value => value.IsResolved).Returns(true);
            tenant.SetupGet(value => value.TenantId).Returns(tenantId);
            var configurations = new Mock<IConnectorConfigurationService>();
            configurations.Setup(value => value.GetSecretsAsync(
                    "GoogleHealth", It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new Dictionary<string, string>(secrets, StringComparer.OrdinalIgnoreCase));
            configurations.Setup(value => value.SaveSecretsAsync(
                    "GoogleHealth", It.IsAny<Dictionary<string, string>>(), null,
                    It.IsAny<CancellationToken>()))
                .Callback<string, Dictionary<string, string>, string?, CancellationToken>((_, values, _, _) =>
                    secrets = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase))
                .Returns(Task.CompletedTask);
            configurations.Setup(value => value.GetConfigurationAsync(
                    "GoogleHealth", It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new ConnectorConfigurationResponse
                {
                    ConnectorName = "GoogleHealth",
                    Configuration = JsonDocument.Parse(StoredConfiguration.Contains("importFrom") ? StoredConfiguration : "{\"importFrom\":\"2000-01-01T00:00:00Z\"}")
                });
            configurations.Setup(value => value.SaveConfigurationAsync(
                    "GoogleHealth", It.IsAny<JsonDocument>(), null, It.IsAny<CancellationToken>()))
                .Callback<string, JsonDocument, string?, CancellationToken>((_, document, _, _) =>
                {
                    ConfigurationSaves++;
                    ImportFromWasConsumed = document.RootElement.GetProperty("importFrom").ValueKind == JsonValueKind.Null;
                    LastSavedConfiguration = document.RootElement.GetRawText();
                    StoredConfiguration = LastSavedConfiguration;
                })
                .ReturnsAsync(() => new ConnectorConfigurationResponse());
            var cursorStore = new Mock<IConnectorSyncCursorStore>();
            cursorStore.Setup(value => value.GetAsync("GoogleHealth", "health", It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new ConnectorSyncCursor(null, StoredConfiguration));
            cursorStore.Setup(value => value.SetAsync("GoogleHealth", "health", It.IsAny<ConnectorSyncCursor>(), It.IsAny<CancellationToken>()))
                .Callback<string, string, ConnectorSyncCursor, CancellationToken>((_, _, cursor, _) =>
                {
                    var prior = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(StoredConfiguration)!;
                    foreach (var (key, value) in JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(cursor.LastGuid!)!) prior[key] = value;
                    LastSavedConfiguration = JsonSerializer.Serialize(prior);
                    StoredConfiguration = LastSavedConfiguration;
                }).Returns(Task.CompletedTask);
            var coordinator = Coordinator;
            coordinator.Setup(value => value.AcquireAsync(tenantId, It.IsAny<CancellationToken>(), null))
                .Returns(async (Guid _, CancellationToken ct, TimeSpan? _) =>
                {
                    await Gate.WaitAsync(ct);
                    return new TestLease(Gate);
                });
            Writer = new Mock<IGoogleHealthReadingWriter>();
            Writer.Setup(value => value.WriteAsync(
                    It.IsAny<IReadOnlyCollection<GoogleHealthReading>>(),
                    It.IsAny<IReadOnlyCollection<Nocturne.Core.Models.SleepSession>>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            Writer.Setup(value => value.BeginReconciliationAsync(
                    It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<DateTimeOffset>(),
                    It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Guid.NewGuid());
            var handler = new StubHandler(responder);
            var loader = new Mock<IConnectorConfigurationLoader<GoogleHealthConnectorConfiguration>>();
            loader.Setup(value => value.LoadForTenantAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() =>
            {
                var loaded = JsonSerializer.Deserialize<GoogleHealthConnectorConfiguration>(
                    JsonSerializer.Serialize(currentConfiguration!))!;
                loaded.RefreshToken = secrets.GetValueOrDefault("refreshToken");
                loaded.GrantedScopes = secrets.GetValueOrDefault("grantedScopes");
                return loaded;
            });
            var oauth = new GoogleHealthAuthTokenProvider(
                new HttpClient(handler, false),
                new ConnectorTokenCache(),
                new ConnectorServerResolver<GoogleHealthConnectorConfiguration>(null, null, null),
                tenant.Object,
                NullLogger<GoogleHealthAuthTokenProvider>.Instance);
            Service = new GoogleHealthConnectorService(
                new HttpClient(),
                new ConnectorServerResolver<GoogleHealthConnectorConfiguration>(null, null, null),
                new GoogleHealthClient(new HttpClient(handler, false)),
                oauth,
                Writer.Object,
                coordinator.Object,
                configurations.Object,
                cursorStore.Object,
                tenant.Object,
                loader.Object,
                NullLogger<GoogleHealthConnectorService>.Instance, timeProvider: timeProvider);
        }

        public GoogleHealthConnectorService Service { get; }
        public SemaphoreSlim Gate { get; } = new(1);
        public Mock<IGoogleHealthSyncCoordinator> Coordinator { get; } = new();
        public Mock<IGoogleHealthReadingWriter> Writer { get; }
        public IReadOnlyDictionary<string, string> Secrets => secrets;
        public int ConfigurationSaves { get; private set; }
        public bool ImportFromWasConsumed { get; private set; }
        public string? LastSavedConfiguration { get; private set; }
        public string StoredConfiguration { get; set; } = "{\"importFrom\":\"2000-01-01T00:00:00.0000000+00:00\"}";

        public void SetSession(string? refreshToken, string scopes)
        {
            if (refreshToken is null) secrets.Remove("refreshToken");
            else secrets["refreshToken"] = refreshToken;
            secrets["grantedScopes"] = scopes;
        }

        public GoogleHealthConnectorConfiguration Configuration() => currentConfiguration = new()
        {
            ClientId = "client.apps.googleusercontent.com",
            ClientSecret = "secret",
            CallbackUrl = "https://example.test/settings/connectors/google-health/callback",
            RefreshToken = secrets["refreshToken"],
            GrantedScopes = secrets["grantedScopes"],
            SyncSteps = false,
            SyncHeartRate = false,
            SyncBodyWeight = true,
            SyncSleep = false,
            BatchSize = 2
        };
    }

    private sealed class TestLease(SemaphoreSlim gate) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { gate.Release(); return ValueTask.CompletedTask; }
    }

    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(text, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage CaptureRange(
        HttpRequestMessage request,
        Action<DateTimeOffset> capture)
    {
        var filter = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["filter"]!;
        var timestamp = filter.Split('"')[1];
        capture(DateTimeOffset.Parse(timestamp));
        return Json("{\"dataPoints\":[]}");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(responder(request));
    }
}

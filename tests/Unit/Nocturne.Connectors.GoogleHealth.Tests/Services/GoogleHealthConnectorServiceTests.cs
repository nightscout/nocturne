using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.GoogleHealth.Configurations;
using Nocturne.Connectors.GoogleHealth.Services;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Health;
using Nocturne.Tests.Shared.Connectors;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.Connectors.GoogleHealth.Tests.Services;

public class GoogleHealthConnectorServiceTests
{
    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("\"legacy\"")]
    [InlineData("true")]
    [InlineData("1.5")]
    [InlineData("9223372036854775807")]
    [InlineData("2147483647")]
    [InlineData("0")]
    [InlineData("-3")]
    public async Task Unreadable_retry_window_sizes_do_not_stop_the_import(string chunkDays)
    {
        var fixture = new Fixture(request => request.RequestUri!.AbsolutePath == "/token"
            ? Json($$"""{"access_token":"access","expires_in":3600,"scope":"{{GoogleHealthClient.MetricsScope}}"}""")
            : Json("{\"dataPoints\":[]}"));
        fixture.State.Vendor[GoogleHealthState.BackfillChunkDays] = JsonNode.Parse(chunkDays)!;
        var result = await fixture.Service.SyncDataAsync(new SyncRequest(), fixture.Configuration(), default);
        Assert.True(result.Success);
        Assert.True(fixture.State.Resume.ContainsKey(GoogleHealthState.LastSyncedTo));
        Assert.True(fixture.State.Resume.ContainsKey(GoogleHealthState.BackfillCursor));
    }

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

    [Fact]
    public async Task First_live_import_uses_one_clock_snapshot_at_utc_midnight()
    {
        var now = new DateTimeOffset(2026, 9, 30, 23, 59, 59, TimeSpan.Zero);
        var clock = new MidnightTimeProvider(now);
        var fixture = new Fixture(request => request.RequestUri!.AbsolutePath == "/token"
            ? Json($$"""{"access_token":"access","expires_in":3600,"scope":"{{GoogleHealthClient.MetricsScope}}"}""")
            : Json("{\"dataPoints\":[]}"), clock);
        var config = fixture.Configuration();
        config.ImportFrom = now.UtcDateTime.Date.ToString("O");

        var result = await fixture.Service.SyncDataAsync(new SyncRequest(), config, default);

        Assert.True(result.Success);
        Assert.Equal(1, clock.Reads);
        fixture.Writer.Verify(writer => writer.BeginReconciliationAsync(
            It.IsAny<IReadOnlyCollection<string>>(),
            new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero), now,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private sealed class MidnightTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        public int Reads { get; private set; }
        public override DateTimeOffset GetUtcNow() => initial.AddSeconds(Reads++);
    }

    /// <summary>A sub-second instant mid-month, so a test's day and month arithmetic and the live
    /// window's minute rounding read the same clock as the service.</summary>
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 4, 24, 59, 999, TimeSpan.Zero);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
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
        Assert.Empty(fixture.State.Resume);
    }

    [Fact]
    public async Task A_session_from_a_former_client_is_revoked_and_cleared_without_reading()
    {
        var requests = new List<string>();
        var fixture = new Fixture(request =>
        {
            requests.Add(request.RequestUri!.AbsolutePath + " " + request.Content?.ReadAsStringAsync().GetAwaiter().GetResult());
            return Json("{}");
        });
        fixture.SetSession("refresh", GoogleHealthClient.MetricsScope);
        var config = fixture.Configuration();
        config.SessionClientId = "former.apps.googleusercontent.com";

        var result = await fixture.Service.SyncDataAsync(new SyncRequest(), config, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("client_changed", result.Message);
        Assert.Equal(["/revoke token=refresh"], requests);
        Assert.False(fixture.Secrets.ContainsKey("refreshToken"));
    }

    [Fact]
    public async Task Disabled_connector_skips_polling_without_a_refresh_token()
    {
        var fixture = new Fixture(_ => throw new InvalidOperationException("Google must not be called"));
        var config = fixture.Configuration();
        config.Enabled = false;
        config.RefreshToken = null;
        fixture.SetSession(null, "");

        var result = await fixture.Service.SyncDataAsync(new SyncRequest(), config, CancellationToken.None);

        Assert.True(result.Success);
        fixture.Writer.Verify(value => value.BeginReconciliationAsync(
            It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<DateTimeOffset>(),
            It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.Writer.Verify(value => value.WriteAsync(
            It.IsAny<IReadOnlyCollection<GoogleHealthReading>>(),
            It.IsAny<IReadOnlyCollection<Nocturne.Core.Models.SleepSession>>(),
            It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Google_revoked_refresh_token_disables_further_polls_and_preserves_import_settings()
    {
        var calls = 0;
        var fixture = new Fixture(_ =>
        {
            ++calls;
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":"invalid_grant"}""", Encoding.UTF8, "application/json")
            };
        });
        fixture.SetSession("revoked-refresh", GoogleHealthClient.MetricsScope);
        fixture.StoredConfiguration = """{"enabled":true,"importFrom":"2026-01-01T00:00:00Z","historyDays":14,"sentinel":"kept"}""";
        var config = fixture.Configuration();
        var result = await fixture.Service.SyncDataAsync(new SyncRequest(), config, default);
        Assert.Equal("reconnect_required", result.Message);
        Assert.False(fixture.Secrets.ContainsKey("refreshToken"));
        using var saved = JsonDocument.Parse(fixture.StoredConfiguration);
        Assert.Equal("2026-01-01T00:00:00Z", saved.RootElement.GetProperty("importFrom").GetString());
        Assert.Equal(14, saved.RootElement.GetProperty("historyDays").GetInt32());
        Assert.Equal("kept", saved.RootElement.GetProperty("sentinel").GetString());
        Assert.True((await fixture.Service.SyncDataAsync(new SyncRequest(), config, default)).Success);
        Assert.Equal(1, calls);
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
        Assert.True(fixture.State.Resume.ContainsKey(GoogleHealthState.LastSyncedTo));
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
        }, new FixedTimeProvider(Now));
        fixture.StoredConfiguration = JsonSerializer.Serialize(new { importFrom = (string?)null });
        fixture.State.Resume[GoogleHealthState.LastSyncedTo] = Utc(watermark);

        var result = await fixture.Service.SyncDataAsync(fixture.Configuration(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(new DateTimeOffset(2026, 9, 10, 9, 55, 0, TimeSpan.Zero), requestedFrom[0]);
        var today = Now.UtcDateTime.Date;
        var monthStart = new DateTime(today.AddDays(-1).Year, today.AddDays(-1).Month, 1);
        Assert.Equal(today.AddDays(-7) > monthStart ? today.AddDays(-7) : monthStart, requestedFrom[1].UtcDateTime.Date);
    }

    [Fact]
    public async Task After_a_purge_that_keeps_imports_out_the_next_sync_continues_from_the_kept_position()
    {
        var requestedFrom = new List<DateTimeOffset>();
        var fixture = new Fixture(request => request.RequestUri!.AbsolutePath switch
        {
            "/token" => Json($$"""{"access_token":"access","refresh_token":"refresh","expires_in":3600,"token_type":"Bearer","scope":"{{GoogleHealthClient.MetricsScope}}"}"""),
            var path when path.Contains("/weight/") => CaptureRange(request, requestedFrom.Add),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
        }, new FixedTimeProvider(Now));
        var config = fixture.Configuration();
        config.ImportFrom = "2000-01-01T00:00:00.0000000+00:00";
        var lastSyncedTo = new DateTime(2026, 10, 21, 3, 55, 0, DateTimeKind.Utc);
        var cursor = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        fixture.State.Resume[GoogleHealthState.LastSyncedTo] = lastSyncedTo;
        fixture.State.Resume[GoogleHealthState.BackfillCursor] = cursor;
        fixture.State.Resume[GoogleHealthState.BackfillFloor] = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        fixture.State.Vendor[GoogleHealthState.BackfillChunkDays] = 4;

        await fixture.State.ClearAsync(
            DataSources.GoogleHealthConnector,
            ConnectorRegistrationAttribute.DeclaredOn(typeof(GoogleHealthConnectorConfiguration)).KeptOutPurgeClears);
        var result = await fixture.Service.SyncDataAsync(config, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(lastSyncedTo.AddMinutes(-5), requestedFrom[0].UtcDateTime);
        Assert.Equal(cursor.AddDays(-4), requestedFrom[1].UtcDateTime);
        Assert.Equal(2, requestedFrom.Count);
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
        fixture.StoredConfiguration = JsonSerializer.Serialize(new { importFrom = (string?)null });
        fixture.State.Resume[GoogleHealthState.LastSyncedTo] = Utc(watermark);
        var result = await fixture.Service.SyncDataAsync(new SyncRequest
        {
            From = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            To = new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc)
        }, fixture.Configuration(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(Utc(watermark), fixture.State.Resume[GoogleHealthState.LastSyncedTo]);
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
        Assert.False(fixture.State.Resume.ContainsKey(GoogleHealthState.LastSyncedTo));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Requested_data_types_do_not_advance_shared_progress(bool bounded)
    {
        var fixture = new Fixture(request => request.RequestUri!.AbsolutePath switch
        {
            "/token" => Json($$"""{"access_token":"access","refresh_token":"refresh","expires_in":3600,"token_type":"Bearer","scope":"{{GoogleHealthClient.MetricsScope}} {{GoogleHealthClient.ActivityScope}}"}"""),
            var path when path.Contains("/weight/") => Json("{\"dataPoints\":[]}"),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
        });
        var config = fixture.Configuration();
        config.SyncSteps = true;

        var originalState = fixture.StoredConfiguration;
        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest
            {
                DataTypes = [SyncDataType.BodyWeight],
                From = bounded ? new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc) : null,
                To = bounded ? new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc) : null,
            },
            config,
            CancellationToken.None);

        Assert.True(result.Success);
        fixture.Writer.Verify(value => value.WriteAsync(
            It.IsAny<IReadOnlyCollection<GoogleHealthReading>>(),
            It.IsAny<IReadOnlyCollection<Nocturne.Core.Models.SleepSession>>(),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Exactly(bounded ? 1 : 2));
        Assert.Equal(originalState, fixture.StoredConfiguration);
        Assert.Empty(fixture.State.Resume);
        Assert.Empty(fixture.State.Vendor);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Open_ended_repairs_use_the_requested_from_through_now(bool sinceOverload)
    {
        var now = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
        var from = now.AddMonths(-2);
        var fixture = new Fixture(request => request.RequestUri!.AbsolutePath == "/token"
            ? Json($$"""{"access_token":"access","expires_in":3600,"scope":"{{GoogleHealthClient.MetricsScope}}"}""")
            : Json("{\"dataPoints\":[]}"), new MidnightTimeProvider(now));
        var config = fixture.Configuration();
        var result = sinceOverload
            ? await fixture.Service.SyncDataAsync(config, default, from.UtcDateTime)
            : await fixture.Service.SyncDataAsync(new SyncRequest { From = from.UtcDateTime }, config, default);

        // The clock moves a second per read, and the scheduled entry point reads it once more than
        // the requested one before the window is resolved.
        Assert.True(result.Success);
        fixture.Writer.Verify(writer => writer.BeginReconciliationAsync(
            It.IsAny<IReadOnlyCollection<string>>(), from,
            It.Is<DateTimeOffset>(to => to >= now && to <= now.AddSeconds(2)), It.IsAny<CancellationToken>()), Times.Once);
        Assert.False(fixture.ImportFromWasConsumed);
        Assert.False(fixture.State.Resume.ContainsKey(GoogleHealthState.BackfillCursor));
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
        }, new FixedTimeProvider(Now));
        var config = fixture.Configuration();
        config.ImportFrom = "2000-01-01T00:00:00.0000000+00:00";
        config.HistoryDays = 7;

        var result = await fixture.Service.SyncDataAsync(config, CancellationToken.None);

        Assert.True(result.Success);
        var today = Now.UtcDateTime.Date;
        Assert.Equal(today, requestedFrom[0].UtcDateTime.Date);
        var firstMonth = new DateTime(today.AddDays(-1).Year, today.AddDays(-1).Month, 1);
        Assert.Equal(firstMonth, requestedFrom[1].UtcDateTime.Date);

        var repeated = await fixture.Service.SyncDataAsync(config, CancellationToken.None);

        Assert.True(repeated.Success);
        Assert.InRange(requestedFrom[2], Now.AddMinutes(-6), Now);
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
                : Json("""{"dataPoints":[]}"""), new FixedTimeProvider(Now));
        var config = fixture.Configuration();
        if (noHistory) config.ImportFrom = Now.UtcDateTime.Date.ToString("O");

        var result = await fixture.Service.SyncDataAsync(new SyncRequest(), config, default);

        Assert.False(result.Success);
        Assert.False(fixture.State.Resume.ContainsKey(GoogleHealthState.BackfillCursor));
        Assert.False(fixture.State.Resume.ContainsKey(GoogleHealthState.BackfillFloor));
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
        }, new FixedTimeProvider(Now));
        var config = fixture.Configuration();
        config.HistoryDays = 7;

        var today = Now.UtcDateTime.Date;
        var monthStart = new DateTime(today.AddDays(-1).Year, today.AddDays(-1).Month, 1);
        var windowFrom = today.AddDays(-7) > monthStart ? today.AddDays(-7) : monthStart;
        var retryDays = Math.Max(1, (int)Math.Ceiling((today - windowFrom).TotalDays / 2));

        var failed = await fixture.Service.SyncDataAsync(config, CancellationToken.None);

        Assert.False(failed.Success);
        Assert.Equal(retryDays, fixture.State.Vendor[GoogleHealthState.BackfillChunkDays].GetValue<int>());
        Assert.True(fixture.State.Resume.ContainsKey(GoogleHealthState.LastSyncedTo));
        Assert.False(fixture.State.Resume.ContainsKey(GoogleHealthState.BackfillCursor));

        failHistoricalWindow = false;
        retryRanges.Clear();
        var retried = await fixture.Service.SyncDataAsync(config, CancellationToken.None);

        Assert.True(retried.Success);
        Assert.InRange(retryRanges[0], Now.AddMinutes(-6), Now);
        Assert.Equal(today.AddDays(-retryDays), retryRanges[1].UtcDateTime.Date);
    }

    [Fact]
    public async Task A_retry_window_save_that_outlasts_thirty_seconds_is_cancelled_and_not_stored()
    {
        var weightRequests = 0;
        var clock = new FakeTimeProvider(Now);
        Fixture? fixture = null;
        var saveStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture = new Fixture(request => request.RequestUri!.AbsolutePath switch
        {
            "/token" => Json($$"""{"access_token":"access","refresh_token":"refresh","expires_in":3600,"token_type":"Bearer","scope":"{{GoogleHealthClient.MetricsScope}}"}"""),
            var path when path.Contains("/weight/") && ++weightRequests == 2 => FailAndStall(),
            var path when path.Contains("/weight/") => Json("""{"dataPoints":[]}"""),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
        }, clock);
        HttpResponseMessage FailAndStall()
        {
            fixture!.State.WriteHook = async token =>
            {
                saveStarted.TrySetResult(token);
                await Task.Delay(Timeout.Infinite, token);
            };
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }
        var config = fixture.Configuration();
        config.HistoryDays = 7;

        var sync = fixture.Service.SyncDataAsync(config, CancellationToken.None);
        var cleanupToken = await saveStarted.Task.WaitAsync(Eventually.DefaultPatience);
        Assert.False(cleanupToken.IsCancellationRequested);
        clock.Advance(TimeSpan.FromSeconds(30));
        var result = await sync.WaitAsync(Eventually.DefaultPatience);

        Assert.True(cleanupToken.IsCancellationRequested);
        Assert.False(result.Success);
        Assert.False(fixture.State.Vendor.ContainsKey(GoogleHealthState.BackfillChunkDays));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_retry_window_save_that_the_store_refuses_leaves_the_sync_failing_for_its_own_reason(bool defect)
    {
        var weightRequests = 0;
        Fixture? fixture = null;
        fixture = new Fixture(request => request.RequestUri!.AbsolutePath switch
        {
            "/token" => Json($$"""{"access_token":"access","refresh_token":"refresh","expires_in":3600,"token_type":"Bearer","scope":"{{GoogleHealthClient.MetricsScope}}"}"""),
            var path when path.Contains("/weight/") && ++weightRequests == 2 => FailAndRefuseWrites(),
            var path when path.Contains("/weight/") => Json("""{"dataPoints":[]}"""),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
        }, new FixedTimeProvider(Now));
        HttpResponseMessage FailAndRefuseWrites()
        {
            fixture!.State.WriteHook = _ => defect
                ? throw new InvalidCastException("defect")
                : throw new ConnectorStateUnavailableException("connection refused", new InvalidOperationException());
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }
        var config = fixture.Configuration();
        config.HistoryDays = 7;

        var result = await fixture.Service.SyncDataAsync(config, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(defect ? "internal_sync" : "google_unavailable:weight", result.Errors.Single());
        Assert.False(fixture.State.Vendor.ContainsKey(GoogleHealthState.BackfillChunkDays));
    }

    [Fact]
    public async Task A_reconciliation_cleanup_that_outlasts_thirty_seconds_is_cancelled()
    {
        var clock = new FakeTimeProvider(Now);
        var fixture = new Fixture(request => request.RequestUri!.AbsolutePath switch
        {
            "/token" => Json($$"""{"access_token":"access","refresh_token":"refresh","expires_in":3600,"token_type":"Bearer","scope":"{{GoogleHealthClient.MetricsScope}}"}"""),
            var path when path.Contains("/weight/") => Json(
                """{"dataPoints":[{"name":"weight-1","weight":{"sampleTime":{"physicalTime":"2026-09-01T10:00:00Z"},"weightGrams":70000}}]}"""),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
        }, clock);
        fixture.Writer
            .Setup(value => value.WriteAsync(
                It.IsAny<IReadOnlyCollection<GoogleHealthReading>>(),
                It.IsAny<IReadOnlyCollection<Nocturne.Core.Models.SleepSession>>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database unavailable"));
        var abandonStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Writer
            .Setup(value => value.AbandonReconciliationAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(async (Guid _, CancellationToken token) =>
            {
                abandonStarted.TrySetResult(token);
                await Task.Delay(Timeout.Infinite, token);
            });

        var from = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var sync = fixture.Service.SyncDataAsync(
            new SyncRequest { From = from, To = from.AddDays(1) }, fixture.Configuration(), CancellationToken.None);
        var cleanupToken = await abandonStarted.Task.WaitAsync(Eventually.DefaultPatience);
        Assert.False(cleanupToken.IsCancellationRequested);
        clock.Advance(TimeSpan.FromSeconds(30));
        var result = await sync.WaitAsync(Eventually.DefaultPatience);

        Assert.True(cleanupToken.IsCancellationRequested);
        Assert.False(result.Success);
        Assert.Equal("internal_sync_native_write:weight", result.Message);
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
        Assert.Empty(fixture.State.Resume);
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

    [Theory]
    [InlineData(0, "TTTT")]
    [InlineData(60, "FFTT")]
    public async Task Each_stored_range_is_finished_exactly_when_its_cursor_has_reached_its_floor(int historyDays, string expected)
    {
        var fixture = new Fixture(request => request.RequestUri!.AbsolutePath == "/token"
            ? Json($$"""{"access_token":"access","expires_in":3600,"scope":"{{GoogleHealthClient.MetricsScope}}"}""")
            : Json("""{"dataPoints":[]}"""), new FixedTimeProvider(Now));
        var config = fixture.Configuration();
        config.HistoryDays = historyDays;

        var finished = "";
        for (var attempt = 0; attempt < expected.Length; attempt++)
        {
            Assert.True((await fixture.Service.SyncDataAsync(config, CancellationToken.None)).Success);
            var cursor = fixture.State.Resume[GoogleHealthState.BackfillCursor];
            var floor = fixture.State.Resume[GoogleHealthState.BackfillFloor];
            Assert.Equal(cursor <= floor, GoogleHealthState.BackfillComplete(cursor, floor));
            finished += GoogleHealthState.BackfillComplete(cursor, floor) ? "T" : "F";
        }

        Assert.Equal(expected, finished);
        Assert.Equal(Now.UtcDateTime.Date.AddDays(-historyDays), fixture.State.Resume[GoogleHealthState.BackfillFloor]);
    }

    [Fact]
    public async Task A_floor_moved_below_a_finished_range_reopens_the_historical_import()
    {
        var requestedFrom = new List<DateTimeOffset>();
        var fixture = new Fixture(request => request.RequestUri!.AbsolutePath switch
        {
            "/token" => Json($$"""{"access_token":"access","refresh_token":"refresh","expires_in":3600,"token_type":"Bearer","scope":"{{GoogleHealthClient.MetricsScope}}"}"""),
            var path when path.Contains("/weight/") => CaptureRange(request, requestedFrom.Add),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
        }, new FixedTimeProvider(Now));
        var finishedAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var widerFloor = finishedAt.AddDays(-10);
        fixture.State.Resume[GoogleHealthState.LastSyncedTo] = Now.UtcDateTime.AddMinutes(-10);
        fixture.State.Resume[GoogleHealthState.BackfillCursor] = finishedAt;
        fixture.State.Resume[GoogleHealthState.BackfillFloor] = widerFloor;
        Assert.False(GoogleHealthState.BackfillComplete(finishedAt, widerFloor));

        var result = await fixture.Service.SyncDataAsync(fixture.Configuration(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, requestedFrom.Count);
        Assert.Equal(widerFloor, requestedFrom[1].UtcDateTime);
        Assert.Equal(widerFloor, fixture.State.Resume[GoogleHealthState.BackfillCursor]);
        Assert.True(GoogleHealthState.BackfillComplete(
            fixture.State.Resume[GoogleHealthState.BackfillCursor], fixture.State.Resume[GoogleHealthState.BackfillFloor]));
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
        Assert.False(fixture.State.Resume.ContainsKey(GoogleHealthState.LastSyncedTo));
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
        fixture.State.Resume[GoogleHealthState.LastSyncedTo] = Utc("2026-09-01T10:05:37.123Z");
        fixture.State.Resume[GoogleHealthState.BackfillCursor] = Utc("2026-09-01T00:00:00Z");
        fixture.State.Resume[GoogleHealthState.BackfillFloor] = Utc("2026-09-01T00:00:00Z");
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
        Assert.Equal(from.AddDays(1), fixture.State.Resume[GoogleHealthState.LastSyncedTo]);
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
    public async Task Sync_writes_nothing_while_another_holder_has_the_gate()
    {
        var fixture = new Fixture(request => request.RequestUri!.AbsolutePath == "/token"
            ? Json($$"""{"access_token":"access","expires_in":3600,"scope":"{{GoogleHealthClient.MetricsScope}}"}""")
            : Json("{\"dataPoints\":[]}"));
        var config = fixture.Configuration();
        var gateReleased = false;
        bool? wroteAfterRelease = null;
        fixture.Writer.Setup(value => value.BeginReconciliationAsync(
                It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<DateTimeOffset>(),
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Callback(() => wroteAfterRelease ??= gateReleased)
            .ReturnsAsync(() => Guid.NewGuid());
        await fixture.Gate.WaitAsync();
        var pending = fixture.Service.SyncDataAsync(new SyncRequest(), config, default);
        Assert.False(pending.IsCompleted);
        gateReleased = true;
        fixture.Gate.Release();

        Assert.True((await pending).Success);
        Assert.True(wroteAfterRelease);
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

        Assert.True(result.Success);
        Assert.False(fixture.Secrets.ContainsKey("refreshToken"));
        var skipped = await fixture.Service.SyncDataAsync(new SyncRequest(), config, default);
        Assert.True(skipped.Success);
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
            ["grantedScopes"] = GoogleHealthClient.MetricsScope,
            ["accountKey"] = "account"
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
                    It.IsAny<Nocturne.Core.Contracts.Connectors.ConnectorSecretSaver?>(), It.IsAny<CancellationToken>()))
                .Callback<string, Dictionary<string, string>, string?, Nocturne.Core.Contracts.Connectors.ConnectorSecretSaver?, CancellationToken>((_, values, _, _, _) =>
                    secrets = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase))
                .ReturnsAsync(new Nocturne.Core.Contracts.Connectors.ConnectorSecretsSaveResult());
            configurations.Setup(value => value.SaveRuntimeSecretsAsync(
                    "GoogleHealth", It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()))
                .Callback<string, Dictionary<string, string>, CancellationToken>((_, values, _) =>
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
            var coordinator = Coordinator;
            coordinator.Setup(value => value.WatchReadsAsync(tenantId, It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid _, CancellationToken ct) => new TestReadLease(ct));
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
                loaded.Enabled = !string.IsNullOrWhiteSpace(loaded.RefreshToken);
                return loaded;
            });
            var oauth = new GoogleHealthAuthTokenProvider(
                new HttpClient(handler, false),
                new ConnectorTokenCache(),
                new ConnectorServerResolver<GoogleHealthConnectorConfiguration>(null, null, null),
                tenant.Object,
                TestPlatform.Options(),
                NullLogger<GoogleHealthAuthTokenProvider>.Instance,
                Mock.Of<IRetryDelayStrategy>());
            Service = new GoogleHealthConnectorService(
                new HttpClient(),
                new ConnectorServerResolver<GoogleHealthConnectorConfiguration>(null, null, null),
                new GoogleHealthClient(new HttpClient(handler, false)),
                new GoogleHealthSessionStore(configurations.Object, oauth,
                    Mock.Of<IGoogleHealthGrantHolders>(), tenant.Object, TestPlatform.Options(),
                    NullLogger<GoogleHealthSessionStore>.Instance, TimeProvider.System),
                Writer.Object,
                coordinator.Object,
                configurations.Object,
                tenant.Object,
                loader.Object,
                NullLogger<GoogleHealthConnectorService>.Instance,
                Mock.Of<IRetryDelayStrategy>(),
                publisher: new StoringConnectorPublisher(State),
                timeProvider: timeProvider);
        }

        public GoogleHealthConnectorService Service { get; }
        public HookedStateStore State { get; } = new();
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
            SessionClientId = TestPlatform.ClientId,
            RefreshToken = secrets["refreshToken"],
            GrantedScopes = secrets["grantedScopes"],
            SyncSteps = false,
            SyncHeartRate = false,
            SyncBodyWeight = true,
            SyncSleep = false,
            BatchSize = 2
        };
    }

    private static DateTime Utc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).UtcDateTime;

    /// <summary>The connector's state slots, with a hook that can hold each write.</summary>
    private sealed class HookedStateStore : IConnectorStateStore
    {
        private readonly InMemoryConnectorStateStore inner = new();

        public Dictionary<string, DateTime> Resume => inner.Resume;
        public Dictionary<string, JsonNode> Vendor => inner.Vendor;
        public Func<CancellationToken, Task>? WriteHook { get; set; }

        public Task<IReadOnlyDictionary<string, DateTime>> ResumeAsync(string source, CancellationToken cancellationToken = default) =>
            inner.ResumeAsync(source, cancellationToken);

        public Task<JsonNode?> VendorAsync(string source, string key, CancellationToken cancellationToken = default) =>
            inner.VendorAsync(source, key, cancellationToken);

        public async Task SetResumeAsync(string source, IReadOnlyDictionary<string, DateTime?> changes, CancellationToken cancellationToken = default)
        {
            if (WriteHook is { } hook) await hook(cancellationToken);
            await inner.SetResumeAsync(source, changes, cancellationToken);
        }

        public async Task SetVendorAsync(string source, string key, JsonNode? value, CancellationToken cancellationToken = default)
        {
            if (WriteHook is { } hook) await hook(cancellationToken);
            await inner.SetVendorAsync(source, key, value, cancellationToken);
        }

        public async Task ClearAsync(string source, ConnectorStateSlots slots, CancellationToken cancellationToken = default)
        {
            if (WriteHook is { } hook) await hook(cancellationToken);
            await inner.ClearAsync(source, slots, cancellationToken);
        }
    }

    private sealed class TestLease(SemaphoreSlim gate) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { gate.Release(); return ValueTask.CompletedTask; }
    }

    private sealed class TestReadLease(CancellationToken token) : IGoogleHealthReadLease
    {
        public CancellationToken Token => token;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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

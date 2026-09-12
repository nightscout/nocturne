using System.Net;
using System.Text;
using System.Text.Json;
using Nocturne.Connectors.GoogleHealth.Services;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Health;
using Xunit;

namespace Nocturne.Connectors.GoogleHealth.Tests.Services;

public class GoogleHealthClientTests
{
    [Theory]
    [InlineData("weight", "{\"weight\":{\"sampleTime\":{\"physicalTime\":\"2026-09-01T10:00:00Z\",\"utcOffset\":\"7200s\"},\"weightGrams\":72500}}", "kg", 72.5)]
    [InlineData("heart-rate", "{\"heartRate\":{\"sampleTime\":{\"physicalTime\":\"2026-09-01T10:00:00Z\"},\"beatsPerMinute\":\"67\"}}", "bpm", 67)]
    [InlineData("steps", "{\"steps\":{\"interval\":{\"startTime\":\"2026-09-01T10:00:00Z\",\"endTime\":\"2026-09-01T10:01:00Z\"},\"count\":\"42\"}}", "steps", 42)]
    public void Maps_supported_measurements(
        string type,
        string json,
        string unit,
        decimal expected)
    {
        using var document = JsonDocument.Parse(json);

        var reading = GoogleHealthClient.Parse(type, document.RootElement);

        Assert.Equal(expected, reading.Value);
        Assert.Equal(unit, reading.Unit);
        Assert.Equal(DateTimeOffset.Parse("2026-09-01T10:00:00Z").ToUnixTimeMilliseconds(), reading.Mills);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("dataPointName")]
    public void Retains_the_provider_resource_id_for_same_timestamp_readings(string identityField)
    {
        using var firstDocument = JsonDocument.Parse($$$"""
            {"{{{identityField}}}":"users/me/dataTypes/heart-rate/dataPoints/watch","heartRate":{"sampleTime":{"physicalTime":"2026-09-01T10:00:00Z"},"beatsPerMinute":"72"}}
            """);
        using var secondDocument = JsonDocument.Parse($$$"""
            {"{{{identityField}}}":"users/me/dataTypes/heart-rate/dataPoints/phone","heartRate":{"sampleTime":{"physicalTime":"2026-09-01T10:00:00Z"},"beatsPerMinute":"72"}}
            """);

        var first = GoogleHealthClient.Parse("heart-rate", firstDocument.RootElement);
        var second = GoogleHealthClient.Parse("heart-rate", secondDocument.RootElement);

        Assert.NotEqual(GoogleHealthClient.Key(first), GoogleHealthClient.Key(second));
    }

    [Fact]
    public async Task Skips_a_fractional_heart_rate_without_failing_the_page()
    {
        var handler = new StubHandler(_ => Json("""
            {"dataPoints":[
              {"heartRate":{"sampleTime":{"physicalTime":"2026-09-01T10:00:00Z"},"beatsPerMinute":"72.5"}},
              {"heartRate":{"sampleTime":{"physicalTime":"2026-09-01T10:01:00Z"},"beatsPerMinute":"73"}}
            ]}
            """));
        var client = new GoogleHealthClient(new HttpClient(handler));

        var readings = await ReadAllAsync(client.ReadPagesAsync("token", "heart-rate",
            DateTimeOffset.Parse("2026-09-01T00:00:00Z"), DateTimeOffset.Parse("2026-09-02T00:00:00Z"), default));

        Assert.Single(readings);
        Assert.Equal(73, readings[0].Value);
    }

    [Fact]
    public async Task Pagination_returns_all_valid_pages()
    {
        var calls = 0;
        var handler = new StubHandler(_ => Json(++calls == 1
            ? """{"dataPoints":[{"weight":{"sampleTime":{"physicalTime":"2026-09-01T10:00:00Z"},"weightGrams":70000}}],"nextPageToken":"next"}"""
            : """{"dataPoints":[{"weight":{"sampleTime":{"physicalTime":"2026-09-01T11:00:00Z"},"weightGrams":71000}}]}"""));
        var client = new GoogleHealthClient(new HttpClient(handler));

        var readings = await ReadAllAsync(client.ReadPagesAsync("token", "weight",
            DateTimeOffset.Parse("2026-09-01T00:00:00Z"), DateTimeOffset.Parse("2026-09-02T00:00:00Z"), default));

        Assert.Equal(2, readings.Count);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Maps_sleep_sessions_and_stages()
    {
        using var document = JsonDocument.Parse("""
        {
          "dataPointName":"users/me/dataTypes/sleep/dataPoints/night-1",
          "sleep":{
            "interval":{"startTime":"2026-09-04T22:00:00Z","endTime":"2026-09-05T06:00:00Z"},
            "stages":[
              {"startTime":"2026-09-04T22:00:00Z","endTime":"2026-09-05T02:00:00Z","type":"DEEP"},
              {"startTime":"2026-09-05T02:00:00Z","endTime":"2026-09-05T06:00:00Z","type":"REM"}
            ]
          }
        }
        """);

        var session = GoogleHealthClient.ParseSleep(document.RootElement);

        Assert.Equal(SleepSource.Google, session.Source);
        Assert.Equal("users/me/dataTypes/sleep/dataPoints/night-1", session.OriginalId);
        Assert.Equal(8 * 60 * 60 * 1000, session.TotalSleepMs);
        Assert.Equal(2, session.Stages!.Count);
    }

    [Fact]
    public async Task Pagination_keeps_the_requested_window_and_rejects_cycles()
    {
        var from = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
        var to = from.AddDays(1);
        var calls = 0;
        var handler = new StubHandler(request =>
        {
            Assert.Contains("2026-09-01", Uri.UnescapeDataString(request.RequestUri!.Query));
            calls++;
            return Json(calls == 1
                ? "{\"dataPoints\":[],\"nextPageToken\":\"repeat\"}"
                : "{\"dataPoints\":[],\"nextPageToken\":\"repeat\"}");
        });
        var client = new GoogleHealthClient(new HttpClient(handler));

        var exception = await Assert.ThrowsAsync<GoogleHealthException>(() =>
            ReadAllAsync(client.ReadPagesAsync("token", "weight", from, to, default)));

        Assert.Equal("pagination_failed", exception.Message);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("ACCOUNT_NOT_LINKED", "account_not_linked")]
    [InlineData("MISSING_OAUTH_SCOPE", "permission_denied")]
    [InlineData("INVALID_PAGE_TOKEN", "invalid_google_request")]
    public async Task Provider_errors_are_reduced_to_safe_codes(string reason, string expected)
    {
        var body = JsonSerializer.Serialize(new
        {
            error = new
            {
                message = "sensitive provider detail",
                details = new[] { new { reason } }
            }
        });
        var client = new GoogleHealthClient(new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            })));

        var exception = await Assert.ThrowsAsync<GoogleHealthException>(() => ReadAllAsync(client.ReadPagesAsync(
            "token", "weight", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow, default)));

        Assert.Equal(expected, exception.Message);
        Assert.DoesNotContain("sensitive", exception.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Sleep_scan_and_import_use_end_time_filters_on_every_page(bool inventory)
    {
        var calls = 0;
        var client = new GoogleHealthClient(new HttpClient(new StubHandler(request =>
        {
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
            Assert.Equal("sleep.interval.end_time >= \"2026-09-05T00:00:00Z\" AND sleep.interval.end_time < \"2026-09-06T00:00:00Z\"", query["filter"]);
            Assert.Equal("25", query["pageSize"]);
            Assert.Equal("/v4/users/me/dataTypes/sleep/dataPoints:reconcile", request.RequestUri.AbsolutePath);
            Assert.Equal(calls == 0 ? null : "next", query["pageToken"]);
            calls++;
            return Json(calls == 1 ? "{\"dataPoints\":[],\"nextPageToken\":\"next\"}" : "{\"dataPoints\":[]}");
        })));
        var from = DateTimeOffset.Parse("2026-09-05T00:00:00Z");
        if (inventory)
            Assert.Equal(0, await client.CountAsync("token", "sleep", from, from.AddDays(1), default));
        else
            await foreach (var page in client.ReadSleepPagesAsync("token", from, from.AddDays(1), default))
                Assert.Empty(page);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("2026-09-04T22:00:00Z", "2026-09-05T06:00:00Z", true)]
    [InlineData("2026-09-04T22:00:00Z", "2026-09-05T00:00:00Z", true)]
    [InlineData("2026-09-04T20:00:00Z", "2026-09-04T23:59:59Z", false)]
    [InlineData("2026-09-05T22:00:00Z", "2026-09-06T00:00:00Z", false)]
    [InlineData("2026-09-05T22:00:00Z", "2026-09-06T06:00:00Z", false)]
    public async Task Sleep_import_checks_the_session_end_against_the_half_open_window(string start, string end, bool included)
    {
        var body = JsonSerializer.Serialize(new
        {
            dataPoints = new[] { new { sleep = new { interval = new { startTime = start, endTime = end } } } }
        });
        var client = new GoogleHealthClient(new HttpClient(new StubHandler(_ => Json(body))));
        var sessions = new List<SleepSession>();
        var from = DateTimeOffset.Parse("2026-09-05T00:00:00Z");
        await foreach (var page in client.ReadSleepPagesAsync("token", from, from.AddDays(1), default))
            sessions.AddRange(page);
        Assert.Equal(included ? 1 : 0, sessions.Count);
    }

    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(text, Encoding.UTF8, "application/json")
    };

    private static async Task<List<GoogleHealthReading>> ReadAllAsync(
        IAsyncEnumerable<IReadOnlyCollection<GoogleHealthReading>> pages)
    {
        var readings = new List<GoogleHealthReading>();
        await foreach (var page in pages) readings.AddRange(page);
        return readings;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(responder(request));
    }
}

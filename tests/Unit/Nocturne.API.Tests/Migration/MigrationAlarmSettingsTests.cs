using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Services.Migration;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Tests.Migration;

/// <summary>
/// The alarm thresholds the source states are kept on the run row for the setup hub, and a source
/// that will not state them does not stop the history coming across.
/// </summary>
public class MigrationAlarmSettingsTests
{
    private const string StatusJson = """
        {"status":"ok","settings":{"units":"mmol","alarmUrgentLow":true,"alarmLow":true,"alarmHigh":false,
          "alarmUrgentHigh":true,"alarmTimeagoWarn":true,
          "thresholds":{"bgHigh":260,"bgTargetTop":180,"bgTargetBottom":72,"bgLow":54}}}
        """;

    private sealed class RoutedNightscout(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request.RequestUri!.AbsolutePath));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body = "[]") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static async Task<string?> StoredAlarmSettingsAsync(ServiceProvider provider, Guid jobId)
    {
        await using var db = await provider.GetRequiredService<IDbContextFactory<NocturneDbContext>>().CreateDbContextAsync();
        return await db.MigrationRuns.Where(r => r.Id == jobId).Select(r => r.SourceAlarmSettings).SingleAsync();
    }

    [Fact]
    public async Task ARunStoresTheAlarmSettingsTheSourceStates()
    {
        await using var provider = MigrationJobHarness.BuildProvider(new RoutedNightscout(path =>
            path == "/api/v1/status.json" ? Json(HttpStatusCode.OK, StatusJson) : Json(HttpStatusCode.OK)));

        var status = await MigrationJobHarness.RunAsync(provider, "entries");

        status.State.Should().Be(MigrationJobState.Completed);
        var stored = await StoredAlarmSettingsAsync(provider, status.JobId);
        NightscoutAlarmSettings.FromJson(stored!).Should().Be(NightscoutAlarmSettings.Parse(StatusJson));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "[]")]
    [InlineData(HttpStatusCode.InternalServerError, "[]")]
    [InlineData(HttpStatusCode.OK, "<h1>STATUS OK</h1>")]
    public async Task ASourceThatWillNotStateThemLeavesTheColumnNullAndTheRunCompletes(HttpStatusCode answer, string body)
    {
        await using var provider = MigrationJobHarness.BuildProvider(new RoutedNightscout(path =>
            path == "/api/v1/status.json" ? Json(answer, body) : Json(HttpStatusCode.OK)));

        var status = await MigrationJobHarness.RunAsync(provider, "entries");

        status.State.Should().Be(MigrationJobState.Completed);
        (await StoredAlarmSettingsAsync(provider, status.JobId)).Should().BeNull();
    }
}

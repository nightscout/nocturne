using System.Net;
using System.Text;
using FluentAssertions;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Glooko.Configurations;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Services;

/// <summary>
/// Which failures a Glooko run survives (<see cref="Nocturne.Connectors.Core.Utilities.ConnectorFaults"/>):
/// a malformed record costs that record, a source that will not answer costs that endpoint's types, a
/// connector defect fails the run, and a caller's cancellation is the caller's and is never reported
/// as a failed sync.
/// </summary>
public class GlookoConnectorServiceFaultTests
{
    private const string TempBasalRecord =
        "{\"timestamp\":\"@TS@\",\"duration\":1800,\"rate\":0.5}";

    /// <summary>
    /// A null element in a vendor array is one malformed record. Before it was filtered it raised a
    /// <see cref="NullReferenceException"/>, which fails the run, so every type in the batch was lost on
    /// every cycle for as long as Glooko kept serving it.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_NullRecordInAVendorArray_PublishesItsSiblings()
    {
        var withoutNull = await SyncTempBasals(record => $"{{\"temporaryBasals\":[{record}]}}");
        var withNull = await SyncTempBasals(record => $"{{\"temporaryBasals\":[null,{record},null]}}");

        withNull.Success.Should().BeTrue(string.Join("; ", withNull.Errors));
        withNull.ItemsSynced[SyncDataType.TempBasals]
            .Should().Be(withoutNull.ItemsSynced[SyncDataType.TempBasals])
            .And.BeGreaterThan(0);
    }

    /// <summary>
    /// A device whose settings map is null is one malformed record; the devices beside it still yield
    /// their profile and active-profile span.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SyncDataAsync_NullDeviceSettingsMap_PublishesTheOtherDevicesProfile(bool useV3Api)
    {
        var service = Service(GlookoConstants.V3DeviceSettingsPath, _ => Json(
            """
            {"deviceSettings":{"pumps":{"pump-0":null,"pump-1":{"2026-01-10T00:00:00Z":{
              "basalSettings":{"activeBasalProgram":"Default"},
              "pumpProfilesBasal":[{"segments":{"profileName":"Default","current":true,
                "data":[{"segmentStart":0.0,"duration":24.0,"value":0.8}]}}]}}}}}
            """));

        var result = await service.SyncDataAsync(
            new SyncRequest
            {
                DataTypes = [SyncDataType.Profiles, SyncDataType.StateSpans],
                From = DateTime.UtcNow.AddDays(-3),
            },
            GlookoSyncHarness.Config(useV3Api), CancellationToken.None);

        result.Success.Should().BeTrue(string.Join("; ", result.Errors));
        service.Published.Should().Contain([PublishKind.Profiles, PublishKind.ProfileActivations]);
    }

    /// <summary>
    /// A defect in the connector is not something the next cycle will fix. Skipping it as a fetch
    /// failure would hide it behind a "Failed to fetch" that reads like a Glooko outage.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_ExceptionOutsideTheRecoverableSet_FailsTheRun()
    {
        var service = Service(GlookoConstants.CgmReadingsPath,
            _ => throw new NotSupportedException("connector defect"));

        var result = await service.SyncDataAsync(Request(), GlookoSyncHarness.Config(useV3Api: false),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Message.Should().Be("Sync failed with exception");
        result.Errors.Should().ContainSingle().Which.Should().Be("connector defect");
        service.Published.Should().BeEmpty("the run stopped at the defect rather than skipping past it");
    }

    /// <summary>
    /// HttpClient reports its own timeout as a <see cref="TaskCanceledException"/> while the caller's
    /// token is still live. That is Glooko not answering, which costs only the endpoint's types.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_EndpointTimesOut_ReportsAFetchFailureAndKeepsSyncing()
    {
        var service = Service(GlookoConstants.CgmReadingsPath,
            _ => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"));

        var result = await service.SyncDataAsync(Request(), GlookoSyncHarness.Config(useV3Api: false),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Be("Failed to fetch Glucose");
        service.Published.Should().Contain(PublishKind.TempBasals);
    }

    /// <summary>
    /// A caller who cancels has withdrawn the run. Reporting it as a failed sync would mark the
    /// connector unhealthy for something nobody did wrong.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_CallerCancelsDuringAFetch_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        var service = Service(GlookoConstants.CgmReadingsPath, _ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        await FluentActions.Awaiting(() => service.SyncDataAsync(
                Request(), GlookoSyncHarness.Config(useV3Api: false), cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    private static async Task<SyncResult> SyncTempBasals(Func<string, string> envelope)
    {
        var service = Service(GlookoConstants.TemporaryBasalsPath, request =>
        {
            var start = QueryValue(request, "startDate") ?? DateTime.UtcNow.ToString("O");
            var at = DateTime.Parse(start, null, System.Globalization.DateTimeStyles.AdjustToUniversal)
                .AddHours(2).ToString("yyyy-MM-ddTHH:mm:ssZ");
            return Json(envelope(TempBasalRecord.Replace("@TS@", at)));
        });

        return await service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.TempBasals], From = DateTime.UtcNow.AddDays(-3) },
            GlookoSyncHarness.Config(useV3Api: false), CancellationToken.None);
    }

    private static SyncRequest Request() => new()
    {
        DataTypes = [SyncDataType.Glucose, SyncDataType.TempBasals],
        From = DateTime.UtcNow.AddDays(-3), // single chunk keeps one request per endpoint
    };

    /// <summary>The harness's Glooko, except <paramref name="path"/> answers with <paramref name="respond"/>.</summary>
    private static RecordingGlookoConnectorService Service(
        string path, Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new HttpClient(new OverridingHandler(path, respond)), new StaticGlookoTokenProvider(), rejected: null);

    private sealed class OverridingHandler(string path, Func<HttpRequestMessage, HttpResponseMessage> respond)
        : DelegatingHandler(new GlookoEndpointHandler())
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            request.RequestUri?.AbsolutePath.Contains(path, StringComparison.OrdinalIgnoreCase) == true
                ? Task.FromResult(respond(request))
                : base.SendAsync(request, cancellationToken);
    }

    private static string? QueryValue(HttpRequestMessage request, string key) =>
        (request.RequestUri?.Query ?? string.Empty)
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .Where(pair => pair.Length == 2 && pair[0] == key)
            .Select(pair => Uri.UnescapeDataString(pair[1]))
            .FirstOrDefault();

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };
}

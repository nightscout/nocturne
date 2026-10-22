using System.Globalization;
using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Services;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Timezones;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Timezones;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Services;

/// <summary>
/// The per-sync device-clock probe, driven through <see cref="GlookoConnectorService.SyncDataAsync"/>:
/// which requests it makes, which evidence it records, and when it records none.
/// </summary>
public class GlookoDeviceClockProbeTests
{
    private const string EgvsPath = "/api/v2/cgm/egvs";
    private const string UsersPath = "/api/v2/users";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Probe_ReadsEgvsFromARealUtcWindow(bool ssv2)
    {
        var rig = new Rig(Route(egvs: EgvPage(DateTime.UtcNow.AddHours(-1))));

        await rig.SyncAsync(ssv2);

        AssertWindowStartsNear48HoursAgo(rig.Handler.FirstUpdatedAtOf(EgvsPath));
        rig.RecordedObservations().Should().Contain(o =>
            o.Source == DeviceClockObservationSource.UploadBatch && o.SampleCount == 3);
    }

    [Fact]
    public async Task Probe_AfterAFailure_NextRunRereadsTheWindow()
    {
        var failBoluses = true;
        var rig = new Rig(path =>
            failBoluses && path.Contains("/api/v2/pumps/normal_boluses")
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : Route(egvs: EgvPage(DateTime.UtcNow.AddHours(-1)))(path));

        await rig.SyncAsync(ssv2: true);
        rig.RecordedObservations().Should().NotContain(o => o.Source == DeviceClockObservationSource.UploadBatch);

        failBoluses = false;
        await rig.SyncAsync(ssv2: true);

        rig.RecordedObservations().Should().Contain(o => o.Source == DeviceClockObservationSource.UploadBatch,
            "each run reads its own window, so a failed run loses nothing");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SessionAccountThatIsNotThePatient_RecordsNoEvidence(bool ssv2)
    {
        var rig = new Rig(Route(
            egvs: EgvPage(DateTime.UtcNow.AddHours(-1)),
            usersCode: "someone-elses-code"));

        await rig.SyncAsync(ssv2);

        rig.DeviceClock.Verify(s => s.RecordObservationsAsync(
            It.IsAny<string>(), It.IsAny<IReadOnlyList<DeviceClockObservation>>(),
            It.IsAny<double?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static void AssertWindowStartsNear48HoursAgo(string cursor) =>
        DateTime.Parse(cursor, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)
            .Should().BeCloseTo(DateTime.UtcNow.AddHours(-48), TimeSpan.FromMinutes(2));

    private static Func<string, HttpResponseMessage> Route(string egvs, string? usersCode = null) => path =>
    {
        if (path.Contains(UsersPath))
            return Json(
                $"{{\"users\":[{{\"guid\":\"u1\",\"glookoCode\":\"{usersCode ?? GlookoSyncHarness.PatientCode}\"}}],\"lastPage\":true}}");
        if (path.Contains(EgvsPath))
            return Json(egvs);
        return Json("{\"lastPage\":true}");
    };

    private static string EgvPage(DateTime uploadedAtUtc)
    {
        var sync = uploadedAtUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        var records = string.Join(",", Enumerable.Range(0, 3).Select(i =>
        {
            var clinical = uploadedAtUtc.AddMinutes(-5 * i).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            return $"{{\"displayTime\":\"{clinical}\",\"syncTimestamp\":\"{sync}\",\"glucoseValue\":10000,"
                   + $"\"guid\":\"e{i}\",\"calculated\":false,\"softDeleted\":false}}";
        }));
        return $"{{\"egvs\":[{records}],\"lastPage\":true,\"lastUpdatedAt\":\"{sync}\",\"lastGuid\":\"e0\"}}";
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class Rig
    {
        public Rig(Func<string, HttpResponseMessage> route)
        {
            Handler = new CountingHandler(route);
            DeviceClock
                .Setup(s => s.RecordObservationsAsync(
                    It.IsAny<string>(), It.IsAny<IReadOnlyList<DeviceClockObservation>>(),
                    It.IsAny<double?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
            DeviceClock
                .Setup(s => s.GetObservationsAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([new DeviceClockObservation()]);
            Timeline.Setup(s => s.GetTimelineAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
            Publisher.Setup(p => p.IsAvailable).Returns(true);
            Publisher.Setup(p => p.Glucose.PublishSensorGlucoseAsync(
                    It.IsAny<IEnumerable<SensorGlucose>>(), It.IsAny<string>(),
                    It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
        }

        public CountingHandler Handler { get; }
        public Mock<IDeviceClockService> DeviceClock { get; } = new();
        public Mock<ITimezoneTimelineService> Timeline { get; } = new();
        public Mock<IConnectorPublisher> Publisher { get; } = new();

        public Task<SyncResult> SyncAsync(bool ssv2)
        {
            var config = new GlookoConnectorConfiguration
            {
                ConnectSource = ConnectSource.Glooko,
                Email = "user@example.com",
                Password = "secret",
                Server = GlookoConstants.RegionEU,
                UseSsv2Sync = ssv2,
                UseV3Api = false,
                SyncGlucose = true,
            };
            var service = new GlookoConnectorService(
                new HttpClient(Handler),
                new ConnectorServerResolver<GlookoConnectorConfiguration>(null, null, null),
                NullLogger<GlookoConnectorService>.Instance,
                Mock.Of<IRetryDelayStrategy>(),
                Mock.Of<IRateLimitingStrategy>(),
                new StaticGlookoTokenProvider(),
                Publisher.Object,
                mealMatchingService: null,
                timezoneTimelineService: Timeline.Object,
                deviceClockService: DeviceClock.Object,
                cursorStore: new FakeCursorStore());

            return service.SyncDataAsync(
                new SyncRequest { DataTypes = [SyncDataType.Glucose] }, config, CancellationToken.None);
        }

        public IReadOnlyList<DeviceClockObservation> RecordedObservations() =>
            DeviceClock.Invocations
                .Where(i => i.Method.Name == nameof(IDeviceClockService.RecordObservationsAsync))
                .SelectMany(i => (IReadOnlyList<DeviceClockObservation>)i.Arguments[1])
                .ToList();
    }

    private sealed class CountingHandler(Func<string, HttpResponseMessage> route) : HttpMessageHandler
    {
        private readonly List<string> _paths = [];

        public int RequestsTo(string endpoint) => _paths.Count(p => p.Contains(endpoint));

        public string FirstUpdatedAtOf(string endpoint)
        {
            var path = _paths.First(p => p.Contains(endpoint));
            const string marker = "lastUpdatedAt=";
            var start = path.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
            var end = path.IndexOf('&', start);
            return Uri.UnescapeDataString(end < 0 ? path[start..] : path[start..end]);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            _paths.Add(path);
            return Task.FromResult(route(path));
        }
    }
}

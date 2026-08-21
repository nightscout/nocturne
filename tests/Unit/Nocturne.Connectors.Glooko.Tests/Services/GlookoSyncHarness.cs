using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Services;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.Glooko.Tests.Services;

/// <summary>
/// A Glooko sync driven end to end against fake endpoints: an authenticated session, both fetch
/// paths' endpoints, and a service that intercepts every publish instead of reaching a publisher.
/// </summary>
internal static class GlookoSyncHarness
{
    public const string PatientCode = "eu-west-1-indigo-killdeer-4650";

    public static GlookoConnectorConfiguration Config(bool useV3Api) => new()
    {
        ConnectSource = ConnectSource.Glooko,
        Email = "user@example.com",
        Password = "secret",
        Server = GlookoConstants.RegionEU,
        UseV3Api = useV3Api,
    };

    public static RecordingGlookoConnectorService Service(
        GlookoEndpointHandler handler,
        PublishKind? rejected = null,
        IConnectorPublisher? publisher = null) =>
        new(new HttpClient(handler), new StaticGlookoTokenProvider(), rejected, publisher);
}

/// <summary>
/// The publishes a Glooko sync reaches. System events have no <see cref="SyncDataType"/> of their
/// own and profile state spans share the state-span publish, so both are named separately here.
/// </summary>
public enum PublishKind
{
    StateSpans,
    ProfileStateSpans,
    TempBasals,
    DeviceEvents,
    SystemEvents,
    Profiles,
}

/// <summary>
/// Accepts every publish except the one under test, and records which publishes were reached.
/// </summary>
internal sealed class RecordingGlookoConnectorService : GlookoConnectorService
{
    private readonly PublishKind? _rejected;

    public RecordingGlookoConnectorService(
        HttpClient httpClient, GlookoAuthTokenProvider tokenProvider, PublishKind? rejected,
        IConnectorPublisher? publisher = null)
        : base(
            httpClient,
            new ConnectorServerResolver<GlookoConnectorConfiguration>(null, null, null),
            NullLogger<GlookoConnectorService>.Instance,
            Mock.Of<IRetryDelayStrategy>(),
            Mock.Of<IRateLimitingStrategy>(),
            tokenProvider,
            publisher)
    {
        _rejected = rejected;
    }

    public List<PublishKind> Published { get; } = [];

    // Profile state spans reach the same publish as the chunks' spans; the profile mapper's
    // OriginalId prefix is what tells the two batches apart.
    protected override Task<bool> PublishStateSpanDataAsync(
        IEnumerable<StateSpan> stateSpans, GlookoConnectorConfiguration config,
        CancellationToken cancellationToken = default) =>
        Record(stateSpans.All(s =>
            s.OriginalId?.StartsWith("glooko_active_profile_", StringComparison.Ordinal) == true)
            ? PublishKind.ProfileStateSpans
            : PublishKind.StateSpans);

    protected override Task<bool> PublishTempBasalDataAsync(
        IEnumerable<TempBasal> records, GlookoConnectorConfiguration config,
        CancellationToken cancellationToken = default) => Record(PublishKind.TempBasals);

    protected override Task<bool> PublishDeviceEventDataAsync(
        IEnumerable<DeviceEvent> records, GlookoConnectorConfiguration config,
        CancellationToken cancellationToken = default) => Record(PublishKind.DeviceEvents);

    protected override Task<bool> PublishSystemEventDataAsync(
        IEnumerable<SystemEvent> systemEvents, GlookoConnectorConfiguration config,
        CancellationToken cancellationToken = default) => Record(PublishKind.SystemEvents);

    protected override Task<bool> PublishProfileDataAsync(
        IEnumerable<Profile> profiles, GlookoConnectorConfiguration config,
        CancellationToken cancellationToken = default) => Record(PublishKind.Profiles);

    private Task<bool> Record(PublishKind kind)
    {
        Published.Add(kind);
        return Task.FromResult(kind != _rejected);
    }
}

/// <summary>
/// Issues a fixed session cookie and patient code, standing in for a completed Glooko login.
/// </summary>
internal sealed class StaticGlookoTokenProvider : GlookoAuthTokenProvider
{
    public StaticGlookoTokenProvider()
        : base(
            new HttpClient(),
            new ConnectorTokenCache(),
            new ConnectorServerResolver<GlookoConnectorConfiguration>(null, null, null),
            new FakeTenantAccessor(),
            NullLogger<GlookoAuthTokenProvider>.Instance)
    {
    }

    protected override Task<(string? Token, DateTime ExpiresAt, IReadOnlyDictionary<string, string>? Metadata)> AcquireTokenAsync(
        GlookoConnectorConfiguration config, CancellationToken cancellationToken)
    {
        const string cookie = "_logbook-web_session=sess";
        var userData = JsonSerializer.Serialize(
            new GlookoUserData { User = new GlookoUserLogin { GlookoCode = GlookoSyncHarness.PatientCode } });

        return Task.FromResult<(string?, DateTime, IReadOnlyDictionary<string, string>?)>(
            (cookie, DateTime.UtcNow.AddHours(1), new Dictionary<string, string>
            {
                ["SessionCookie"] = cookie,
                ["UserData"] = userData,
            }));
    }

    private sealed class FakeTenantAccessor : ITenantAccessor
    {
        public bool IsResolved => true;
        public Guid TenantId => Guid.Empty;
        public TenantContext? Context => null;
        public void SetTenant(TenantContext context) { }
    }
}

/// <summary>
/// Serves both fetch paths: the V3 graph plus the V2 pump endpoints, each carrying the same number
/// of suspended basals and temporary basals so the two modes publish the same record types. Device
/// settings are shared — the profile block runs in both modes — and carry no date window.
/// </summary>
/// <param name="recordsPerWindow">
///     How many of each record type a request window carries, by the order in which the sync first
///     asks for that window. Defaults to one per window.
/// </param>
internal sealed class GlookoEndpointHandler(Func<int, int>? recordsPerWindow = null) : HttpMessageHandler
{
    private readonly Func<int, int> _recordsPerWindow = recordsPerWindow ?? (_ => 1);
    private readonly List<string> _windows = [];
    private readonly Lock _gate = new();

    private const string DeviceSettingsTimestamp = "2026-01-10T00:00:00Z";

    /// <summary>How many distinct date windows the sync has asked for.</summary>
    public int WindowCount
    {
        get { lock (_gate) return _windows.Count; }
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.PathAndQuery ?? string.Empty;

        if (Matches(path, GlookoConstants.V3UsersPath))
            return Json("{\"currentUser\":{\"meterUnits\":\"mgdl\",\"timezone\":\"Australia/Sydney\"}}");

        if (Matches(path, GlookoConstants.V3DeviceSettingsPath))
            return Json(DeviceSettingsPayload());

        if (Matches(path, GlookoConstants.V3HistoriesPath))
            return Json("{\"histories\":[]}");

        var window = ResolveWindow(request.RequestUri?.Query ?? string.Empty);

        if (Matches(path, GlookoConstants.V3GraphDataPath))
            return Json(GraphPayload(window));

        if (Matches(path, GlookoConstants.SuspendBasalsPath))
            return Json(Records("suspendBasals", window,
                at => $"{{\"timestamp\":\"{Iso(at)}\",\"duration\":1800}}"));

        if (Matches(path, GlookoConstants.TemporaryBasalsPath))
            return Json(Records("temporaryBasals", window,
                at => $"{{\"timestamp\":\"{Iso(at)}\",\"duration\":1800,\"rate\":0.5}}"));

        if (Matches(path, GlookoConstants.ScheduledBasalsPath))
            return Json("{\"scheduledBasals\":[]}");

        if (Matches(path, GlookoConstants.NormalBolusesPath))
            return Json("{\"normalBoluses\":[]}");

        if (Matches(path, GlookoConstants.CgmReadingsPath))
            return Json("{\"readings\":[]}");

        if (Matches(path, GlookoConstants.FoodsPath))
            return Json(Records("foods", window with { Records = 1 },
                at => $"{{\"guid\":\"food_{at.Ticks}\",\"timestamp\":\"{Iso(at)}\",\"carbs\":30.0,\"name\":\"Toast\"}}"));

        // MeterReadingsPath is a prefix of the CGM path's parent, so it is matched last.
        if (Matches(path, GlookoConstants.MeterReadingsPath))
            return Json("{\"readings\":[]}");

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    /// <summary>A request's date window, and how many records of each type it should carry.</summary>
    private readonly record struct Window(DateTime Start, int Records);

    private Window ResolveWindow(string query)
    {
        var startDate = QueryValue(query, "startDate");
        var start = DateTime.TryParse(startDate, null, System.Globalization.DateTimeStyles.AdjustToUniversal
            | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : DateTime.UtcNow;

        int ordinal;
        lock (_gate)
        {
            ordinal = _windows.IndexOf(startDate ?? string.Empty);
            if (ordinal < 0)
            {
                _windows.Add(startDate ?? string.Empty);
                ordinal = _windows.Count - 1;
            }
        }

        return new Window(start, _recordsPerWindow(ordinal));
    }

    /// <summary>
    /// One suspended basal per record (mapped to both a state span and a temp basal), one reservoir
    /// change (a device event) and one pump alarm (a system event).
    /// </summary>
    private static string GraphPayload(Window window)
    {
        var series = string.Join(",",
            Property("suspendBasal", window, at => $"{{\"x\":{Unix(at)},\"duration\":1800,\"label\":\"Suspended\"}}"),
            Property("reservoirChange", window, at => $"{{\"x\":{Unix(at)},\"label\":\"Reservoir change\"}}"),
            Property("pumpAlarm", window, at => $"{{\"x\":{Unix(at)},\"alarmType\":\"OCCLUSION\",\"label\":\"Occlusion\"}}"));

        return $"{{\"series\":{{{series}}}}}";
    }

    /// <summary>
    /// One settings snapshot carrying a basal segment (which maps to a single profile) and an active
    /// basal program (which maps to a single profile state span).
    /// </summary>
    private static string DeviceSettingsPayload() =>
        """
        {"deviceSettings":{"pumps":{"pump-1":{"@TS@":{
          "basalSettings":{"activeBasalProgram":"Default"},
          "pumpProfilesBasal":[{"segments":{"profileName":"Default","current":true,
            "data":[{"segmentStart":0.0,"duration":24.0,"value":0.8}]}}]
        }}}}}
        """.Replace("@TS@", DeviceSettingsTimestamp);

    /// <summary>
    /// A single-property envelope holding one record per <see cref="Window.Records"/>, each an hour
    /// further into the window so no two share a timestamp.
    /// </summary>
    private static string Records(string property, Window window, Func<DateTime, string> record) =>
        $"{{{Property(property, window, record)}}}";

    private static string Property(string property, Window window, Func<DateTime, string> record) =>
        $"\"{property}\":["
        + string.Join(",", Enumerable.Range(1, window.Records).Select(i => record(window.Start.AddHours(i))))
        + "]";

    private static string Iso(DateTime at) => at.ToString("yyyy-MM-ddTHH:mm:ssZ");

    private static long Unix(DateTime at) => new DateTimeOffset(at, TimeSpan.Zero).ToUnixTimeSeconds();

    private static string? QueryValue(string query, string key) => query
        .TrimStart('?')
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(pair => pair.Split('=', 2))
        .Where(pair => pair.Length == 2 && pair[0] == key)
        .Select(pair => Uri.UnescapeDataString(pair[1]))
        .FirstOrDefault();

    private static bool Matches(string pathAndQuery, string endpoint) =>
        pathAndQuery.Contains(endpoint, StringComparison.OrdinalIgnoreCase);

    private static Task<HttpResponseMessage> Json(string body) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
}

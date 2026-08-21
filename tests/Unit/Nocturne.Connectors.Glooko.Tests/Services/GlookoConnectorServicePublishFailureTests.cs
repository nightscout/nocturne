using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Services;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Services;

/// <summary>
/// A rejected publish must reach <see cref="SyncResult.Success"/> and <see cref="SyncResult.Errors"/>
/// for every data type Glooko syncs, on both the V2 and the V3 fetch path. A tenant whose state spans,
/// temp basals, device events, system events, profiles or food entries never land otherwise sees a
/// green sync with that data missing, indistinguishable from a cycle that had none of it to publish.
/// </summary>
public class GlookoConnectorServicePublishFailureTests
{
    public enum PublishKind
    {
        StateSpans,
        ProfileStateSpans,
        TempBasals,
        DeviceEvents,
        SystemEvents,
        Profiles,
    }

    // Device and system events come from the V3 graph series; the V2 endpoints carry neither.
    [Theory]
    [InlineData(true, PublishKind.StateSpans)]
    [InlineData(true, PublishKind.ProfileStateSpans)]
    [InlineData(true, PublishKind.TempBasals)]
    [InlineData(true, PublishKind.DeviceEvents)]
    [InlineData(true, PublishKind.SystemEvents)]
    [InlineData(true, PublishKind.Profiles)]
    [InlineData(false, PublishKind.StateSpans)]
    [InlineData(false, PublishKind.ProfileStateSpans)]
    [InlineData(false, PublishKind.TempBasals)]
    [InlineData(false, PublishKind.Profiles)]
    public async Task SyncDataAsync_WhenOnePublishIsRejected_ReportsFailure(
        bool useV3Api, PublishKind rejected)
    {
        var service = BuildService(rejected);

        var result = await service.SyncDataAsync(
            BuildRequest(), BuildConfig(useV3Api), CancellationToken.None);

        service.Published.Should().Contain(rejected,
            "the payload must actually reach the publish under test, or the assertions below prove nothing");
        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle();
    }

    /// <summary>
    /// The result is constructed optimistically, so a publish-only failure must not leave the
    /// success literal behind: <see cref="SyncResult.Message"/> is the documented fallback for
    /// consumers that find no <see cref="SyncResult.Errors"/>.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SyncDataAsync_WhenAPublishIsRejected_ReplacesTheOptimisticMessage(bool useV3Api)
    {
        var service = BuildService(PublishKind.StateSpans);

        var result = await service.SyncDataAsync(
            BuildRequest(), BuildConfig(useV3Api), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Message.Should().Be("Sync failed while publishing data");
    }

    /// <summary>
    /// Profile state spans are state spans, so the tenant's StateSpans toggle governs them even
    /// though the device-settings fetch that produces them is gated on Profiles.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SyncDataAsync_WhenStateSpansAreNotRequested_DoesNotPublishProfileStateSpans(
        bool useV3Api)
    {
        var service = BuildService(rejected: null);
        var request = BuildRequest();
        request.DataTypes = [SyncDataType.Profiles];

        var result = await service.SyncDataAsync(request, BuildConfig(useV3Api), CancellationToken.None);

        result.Success.Should().BeTrue();
        service.Published.Should().Contain(PublishKind.Profiles);
        service.Published.Should().NotContain(PublishKind.ProfileStateSpans);
        result.ItemsSynced.Should().NotContainKey(SyncDataType.StateSpans);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SyncDataAsync_WhenEveryPublishIsAccepted_ReportsSuccessAndCountsEachType(
        bool useV3Api)
    {
        var service = BuildService(rejected: null);

        var result = await service.SyncDataAsync(
            BuildRequest(), BuildConfig(useV3Api), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Errors.Should().BeEmpty();
        // One span from the chunk's suspended basal, one from the device settings' active program.
        result.ItemsSynced[SyncDataType.StateSpans].Should().Be(2);
        result.ItemsSynced[SyncDataType.Profiles].Should().Be(1);

        if (useV3Api)
        {
            result.ItemsSynced[SyncDataType.TempBasals].Should().Be(1);
            // Device events and system events both count here — one of each.
            result.ItemsSynced[SyncDataType.DeviceEvents].Should().Be(2);
        }
        else
        {
            // V2 draws temp basals from the temporary-basal and suspend-basal endpoints alike.
            result.ItemsSynced[SyncDataType.TempBasals].Should().Be(2);
            result.ItemsSynced.Should().NotContainKey(SyncDataType.DeviceEvents);
        }
    }

    /// <summary>
    /// The food-entry publisher is metadata-shaped: it answers with the imported entries, and with
    /// <c>null</c> only from its own catch. A rejected import must therefore be reported, and an
    /// import that accepted nothing must not be. Both fetch paths share the publish helper; the V2
    /// endpoints are the ones that carry standalone foods.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenTheFoodEntryPublishFails_ReportsFailure()
    {
        var (publisher, metadata) = BuildFoodPublisher(imported: null);
        var service = BuildService(rejected: null, publisher);

        var result = await service.SyncDataAsync(
            BuildRequest(), BuildConfig(useV3Api: false), CancellationToken.None);

        VerifyFoodEntriesWerePublished(metadata);
        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle()
            .Which.Should().Contain("Food entries");
    }

    [Fact]
    public async Task SyncDataAsync_WhenTheFoodEntryPublishImportsNothing_ReportsSuccess()
    {
        var (publisher, metadata) = BuildFoodPublisher(imported: []);
        var service = BuildService(rejected: null, publisher);

        var result = await service.SyncDataAsync(
            BuildRequest(), BuildConfig(useV3Api: false), CancellationToken.None);

        VerifyFoodEntriesWerePublished(metadata);
        result.Success.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    // ── Test infrastructure ─────────────────────────────────────────────

    private const string PatientCode = "eu-west-1-indigo-killdeer-4650";

    private static readonly long EventUnixSeconds =
        new DateTimeOffset(new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc)).ToUnixTimeSeconds();

    private const string EventTimestamp = "2026-01-10T00:00:00Z";

    /// <summary>
    /// One suspended-basal span (mapped to both a state span and a temp basal), one reservoir change
    /// (a device event) and one pump alarm (a system event).
    /// </summary>
    private static string GraphPayload() => $$$"""
        {"series":{
          "suspendBasal":[{"x":{{{EventUnixSeconds}}},"duration":1800,"label":"Suspended"}],
          "reservoirChange":[{"x":{{{EventUnixSeconds}}},"label":"Reservoir change"}],
          "pumpAlarm":[{"x":{{{EventUnixSeconds}}},"alarmType":"OCCLUSION","label":"Occlusion"}]
        }}
        """;

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
        """.Replace("@TS@", EventTimestamp);

    /// <summary>One standalone V2 food record, which maps to a single connector food entry import.</summary>
    private static string FoodsPayload() =>
        $"{{\"foods\":[{{\"guid\":\"food-1\",\"timestamp\":\"{EventTimestamp}\",\"carbs\":30.0,\"name\":\"Toast\"}}]}}";

    private static SyncRequest BuildRequest() => new()
    {
        DataTypes =
        [
            SyncDataType.StateSpans, SyncDataType.TempBasals,
            SyncDataType.DeviceEvents, SyncDataType.Profiles,
        ],
        From = DateTime.UtcNow.AddDays(-3), // single chunk keeps one request per endpoint
    };

    private static GlookoConnectorConfiguration BuildConfig(bool useV3Api) => new()
    {
        ConnectSource = ConnectSource.Glooko,
        Email = "user@example.com",
        Password = "secret",
        Server = GlookoConstants.RegionEU,
        UseV3Api = useV3Api,
    };

    private static RecordingGlookoConnectorService BuildService(
        PublishKind? rejected, IConnectorPublisher? publisher = null) =>
        new(new HttpClient(new GlookoEndpointHandler()), new StaticGlookoTokenProvider(), rejected, publisher);

    /// <summary>
    /// A publisher whose food-entry import answers <paramref name="imported"/>. Every other publish
    /// the sync reaches is intercepted by <see cref="RecordingGlookoConnectorService"/>.
    /// </summary>
    private static (IConnectorPublisher Publisher, Mock<IMetadataPublisher> Metadata) BuildFoodPublisher(
        IReadOnlyList<ConnectorFoodEntry>? imported)
    {
        var metadata = new Mock<IMetadataPublisher>();
        metadata
            .Setup(m => m.PublishConnectorFoodEntriesAsync(
                It.IsAny<IEnumerable<ConnectorFoodEntryImport>>(), It.IsAny<string>(),
                It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(imported);

        var publisher = new Mock<IConnectorPublisher>();
        publisher.SetupGet(p => p.IsAvailable).Returns(true);
        publisher.SetupGet(p => p.Metadata).Returns(metadata.Object);

        return (publisher.Object, metadata);
    }

    private static void VerifyFoodEntriesWerePublished(Mock<IMetadataPublisher> metadata) =>
        metadata.Verify(
            m => m.PublishConnectorFoodEntriesAsync(
                It.IsAny<IEnumerable<ConnectorFoodEntryImport>>(), It.IsAny<string>(),
                It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "the import must actually be attempted, or the assertions below prove nothing");

    /// <summary>
    /// Accepts every publish except the one under test, and records which publishes were reached.
    /// </summary>
    private sealed class RecordingGlookoConnectorService : GlookoConnectorService
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
    private sealed class StaticGlookoTokenProvider : GlookoAuthTokenProvider
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
                new GlookoUserData { User = new GlookoUserLogin { GlookoCode = PatientCode } });

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
    /// Serves both fetch paths: the V3 graph plus the V2 pump endpoints, each carrying one suspended
    /// basal and one temporary basal so the two modes publish the same record types. Device settings
    /// are shared — the profile block runs in both modes.
    /// </summary>
    private sealed class GlookoEndpointHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;

            if (Matches(path, GlookoConstants.V3UsersPath))
                return Json("{\"currentUser\":{\"meterUnits\":\"mgdl\",\"timezone\":\"Australia/Sydney\"}}");

            if (Matches(path, GlookoConstants.V3GraphDataPath))
                return Json(GraphPayload());

            if (Matches(path, GlookoConstants.V3HistoriesPath))
                return Json("{\"histories\":[]}");

            if (Matches(path, GlookoConstants.V3DeviceSettingsPath))
                return Json(DeviceSettingsPayload());

            if (Matches(path, GlookoConstants.SuspendBasalsPath))
                return Json($"{{\"suspendBasals\":[{{\"timestamp\":\"{EventTimestamp}\",\"duration\":1800}}]}}");

            if (Matches(path, GlookoConstants.TemporaryBasalsPath))
                return Json($"{{\"temporaryBasals\":[{{\"timestamp\":\"{EventTimestamp}\",\"duration\":1800,\"rate\":0.5}}]}}");

            if (Matches(path, GlookoConstants.ScheduledBasalsPath))
                return Json("{\"scheduledBasals\":[]}");

            if (Matches(path, GlookoConstants.NormalBolusesPath))
                return Json("{\"normalBoluses\":[]}");

            if (Matches(path, GlookoConstants.CgmReadingsPath))
                return Json("{\"readings\":[]}");

            if (Matches(path, GlookoConstants.FoodsPath))
                return Json(FoodsPayload());

            // MeterReadingsPath is a prefix of the CGM path's parent, so it is matched last.
            if (Matches(path, GlookoConstants.MeterReadingsPath))
                return Json("{\"readings\":[]}");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static bool Matches(string pathAndQuery, string endpoint) =>
            pathAndQuery.Contains(endpoint, StringComparison.OrdinalIgnoreCase);

        private static Task<HttpResponseMessage> Json(string body) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}

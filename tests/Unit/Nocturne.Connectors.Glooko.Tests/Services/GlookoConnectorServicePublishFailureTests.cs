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
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Services;

/// <summary>
/// A rejected publish must reach <see cref="SyncResult.Success"/> and <see cref="SyncResult.Errors"/>
/// for every data type Glooko syncs. A tenant whose state spans, temp basals, device events or system
/// events never land otherwise sees a green sync with that data missing, indistinguishable from a
/// cycle that had none of it to publish.
/// </summary>
public class GlookoConnectorServicePublishFailureTests
{
    public enum PublishKind
    {
        StateSpans,
        TempBasals,
        DeviceEvents,
        SystemEvents,
    }

    [Theory]
    [InlineData(PublishKind.StateSpans)]
    [InlineData(PublishKind.TempBasals)]
    [InlineData(PublishKind.DeviceEvents)]
    [InlineData(PublishKind.SystemEvents)]
    public async Task SyncDataAsync_WhenOnePublishIsRejected_ReportsFailure(PublishKind rejected)
    {
        var service = BuildService(rejected);

        var result = await service.SyncDataAsync(BuildRequest(), BuildConfig(), CancellationToken.None);

        service.Published.Should().Contain(rejected,
            "the graph payload must actually reach the publish under test, or the assertions below prove nothing");
        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle();
    }

    [Fact]
    public async Task SyncDataAsync_WhenEveryPublishIsAccepted_ReportsSuccessAndCountsEachType()
    {
        var service = BuildService(rejected: null);

        var result = await service.SyncDataAsync(BuildRequest(), BuildConfig(), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Errors.Should().BeEmpty();
        result.ItemsSynced[SyncDataType.StateSpans].Should().Be(1);
        result.ItemsSynced[SyncDataType.TempBasals].Should().Be(1);
        // System events have no SyncDataType of their own and count towards DeviceEvents.
        result.ItemsSynced[SyncDataType.DeviceEvents].Should().Be(2);
    }

    // ── Test infrastructure ─────────────────────────────────────────────

    private const string PatientCode = "eu-west-1-indigo-killdeer-4650";

    /// <summary>
    /// One suspended-basal span (mapped to both a state span and a temp basal), one reservoir change
    /// (a device event) and one pump alarm (a system event).
    /// </summary>
    private static string GraphPayload()
    {
        var x = new DateTimeOffset(new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc)).ToUnixTimeSeconds();
        return $$$"""
            {"series":{
              "suspendBasal":[{"x":{{{x}}},"duration":1800,"label":"Suspended"}],
              "reservoirChange":[{"x":{{{x}}},"label":"Reservoir change"}],
              "pumpAlarm":[{"x":{{{x}}},"alarmType":"OCCLUSION","label":"Occlusion"}]
            }}
            """;
    }

    private static SyncRequest BuildRequest() => new()
    {
        DataTypes = [SyncDataType.StateSpans, SyncDataType.TempBasals, SyncDataType.DeviceEvents],
        From = DateTime.UtcNow.AddDays(-3), // single chunk keeps one graph request
    };

    private static GlookoConnectorConfiguration BuildConfig() => new()
    {
        ConnectSource = ConnectSource.Glooko,
        Email = "user@example.com",
        Password = "secret",
        Server = GlookoConstants.RegionEU,
        UseV3Api = true,
    };

    private static RecordingGlookoConnectorService BuildService(PublishKind? rejected) =>
        new(new HttpClient(new GraphDataHandler()), new StaticGlookoTokenProvider(), rejected);

    /// <summary>
    /// Accepts every publish except the one under test, and records which publishes were reached.
    /// </summary>
    private sealed class RecordingGlookoConnectorService : GlookoConnectorService
    {
        private readonly PublishKind? _rejected;

        public RecordingGlookoConnectorService(
            HttpClient httpClient, GlookoAuthTokenProvider tokenProvider, PublishKind? rejected)
            : base(
                httpClient,
                new ConnectorServerResolver<GlookoConnectorConfiguration>(null, null, null),
                NullLogger<GlookoConnectorService>.Instance,
                Mock.Of<IRetryDelayStrategy>(),
                Mock.Of<IRateLimitingStrategy>(),
                tokenProvider)
        {
            _rejected = rejected;
        }

        public List<PublishKind> Published { get; } = [];

        protected override Task<bool> PublishStateSpanDataAsync(
            IEnumerable<StateSpan> stateSpans, GlookoConnectorConfiguration config,
            CancellationToken cancellationToken = default) => Record(PublishKind.StateSpans);

        protected override Task<bool> PublishTempBasalDataAsync(
            IEnumerable<TempBasal> records, GlookoConnectorConfiguration config,
            CancellationToken cancellationToken = default) => Record(PublishKind.TempBasals);

        protected override Task<bool> PublishDeviceEventDataAsync(
            IEnumerable<DeviceEvent> records, GlookoConnectorConfiguration config,
            CancellationToken cancellationToken = default) => Record(PublishKind.DeviceEvents);

        protected override Task<bool> PublishSystemEventDataAsync(
            IEnumerable<SystemEvent> systemEvents, GlookoConnectorConfiguration config,
            CancellationToken cancellationToken = default) => Record(PublishKind.SystemEvents);

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
    /// Serves the V3 endpoints a sync pass touches, with <see cref="GraphPayload"/> as the graph data.
    /// </summary>
    private sealed class GraphDataHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;

            if (path.Contains("/api/v3/session/users", StringComparison.OrdinalIgnoreCase))
                return Json("{\"currentUser\":{\"meterUnits\":\"mgdl\",\"timezone\":\"Australia/Sydney\"}}");

            if (path.Contains("/api/v3/graph/data", StringComparison.OrdinalIgnoreCase))
                return Json(GraphPayload());

            if (path.Contains("/api/v3/users/summary/histories", StringComparison.OrdinalIgnoreCase))
                return Json("{\"histories\":[]}");

            if (path.Contains("/api/v3/devices_and_settings", StringComparison.OrdinalIgnoreCase))
                return Json("{}");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Json(string body) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}

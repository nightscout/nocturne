using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Services;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Timezones;
using Nocturne.Core.Models.Timezones;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Services;

/// <summary>
/// Pins that a single <see cref="GlookoConnectorService"/> instance cannot leak one tenant's
/// session, patient code, server region or timezone timeline into another tenant's sync. The
/// connector is registered as a typed HTTP client (transient) today, so overlapping runs on one
/// instance are not reachable in production — these tests keep the property structural rather than
/// a consequence of the registration lifetime.
/// </summary>
public class GlookoConnectorServiceStateIsolationTests
{
    private const string TenantACookie = "_logbook-web_session=sess-a";
    private const string TenantBCookie = "_logbook-web_session=sess-b";
    private const string TenantACode = "eu-west-1-indigo-killdeer-4650";
    private const string TenantBCode = "us-east-1-blue-duke-4165";

    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    // One CGM reading at fake-UTC midnight on 2026-01-10, i.e. local midnight in whichever zone the
    // run's own timeline says the person was in. Sydney (AEDT, +11) and Toronto (EST, -5) resolve it
    // to instants 18 hours apart, so a shared time mapper is visible in the result.
    private static readonly DateTime FakeUtcReading = new(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SydneyUtc = new(2026, 1, 9, 13, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime TorontoUtc = new(2026, 1, 10, 5, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task SyncDataAsync_WhenTwoTenantsSyncConcurrentlyOnOneInstance_EachKeepsItsOwnSession()
    {
        // The handler parks whichever run authenticates first inside its user-profile request until a
        // profile request bearing the *other* run's session arrives, so the second run's session is
        // established while the first is mid-flight. The parked run then builds its graph request from
        // whatever state it can still see; if that state is shared, it queries as the other tenant.
        var handler = new InterleavingHandler();
        var service = BuildService(handler);

        await Task.WhenAll(
            RunSyncAsync(service, TenantA, BuildConfig("a@example.com", GlookoConstants.RegionEU)),
            RunSyncAsync(service, TenantB, BuildConfig("b@example.com", GlookoConstants.RegionUS)));

        handler.RunsOverlapped.Should().BeTrue(
            "the parked run must have been released by the other run's session, not by the timeout — "
            + "otherwise the runs never overlapped and the assertion below proves nothing");

        handler.GraphRequests.Should().BeEquivalentTo(
            new[]
            {
                new GlookoRequestIdentity("eu.api.glooko.com", TenantACookie, TenantACode),
                new GlookoRequestIdentity("api.glooko.com", TenantBCookie, TenantBCode),
            },
            "each run must query Glooko with its own host, session cookie and patient code");
    }

    [Fact]
    public async Task SyncDataAsync_WhenTwoTenantsSyncConcurrentlyOnOneInstance_EachMapsOnItsOwnTimezoneTimeline()
    {
        // The sharpest failure mode: a shared time mapper doesn't 401, it silently stamps one tenant's
        // glucose on the other tenant's timezone timeline. Both runs receive the same fake-UTC reading;
        // each must resolve it through the timeline its own tenant's service handed back.
        var handler = new InterleavingHandler();
        var service = BuildService(handler, new AmbientTenantTimezoneTimelineService());

        var configA = BuildConfig("a@example.com", GlookoConstants.RegionEU, includeCgmBackfill: true);
        var configB = BuildConfig("b@example.com", GlookoConstants.RegionUS, includeCgmBackfill: true);

        await Task.WhenAll(
            RunSyncAsync(service, TenantA, configA),
            RunSyncAsync(service, TenantB, configB));

        handler.RunsOverlapped.Should().BeTrue(
            "the runs must actually overlap for the timeline isolation to be under test");

        service.PublishedGlucose.Should().BeEquivalentTo(
            new[] { (TenantA, SydneyUtc), (TenantB, TorontoUtc) },
            "each run must resolve the reading through its own tenant's timeline");
    }

    /// <summary>
    /// Keeps per-sync state off the service type itself. Deliberately narrow — it enforces only that
    /// <see cref="GlookoConnectorService"/> declares no mutable field of its own, instance or static.
    /// It does not (and cannot cheaply) prove a readonly field holds nothing mutable, and
    /// <c>DeclaredOnly</c> excludes <c>BaseConnectorService</c>, which does declare mutable per-run
    /// fields (<c>_glucosePublishOrigin</c>, <c>_treatmentPublishOrigin</c>, <c>_devicePublishOrigin</c>,
    /// <c>_failedRequestCount</c>) whose own comment says they are safe *because* the connector is
    /// resolved fresh per run. That base-class reliance on the registration lifetime is out of this
    /// issue's scope and is not covered here.
    /// </summary>
    [Fact]
    public void GlookoConnectorService_DeclaresNoMutableFields()
    {
        const BindingFlags visibility = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        var instanceFields = typeof(GlookoConnectorService).GetFields(visibility | BindingFlags.Instance);
        var staticFields = typeof(GlookoConnectorService).GetFields(visibility | BindingFlags.Static);

        instanceFields.Where(f => f.IsInitOnly).Should().NotBeEmpty(
            "the service declares its injected dependencies as readonly fields — an empty result would "
            + "mean the reflection query found nothing and the assertions below are vacuous");

        // A conversion to a primary constructor would emit non-readonly private fields for the
        // captured parameters and fail this, even though nothing became shared.
        instanceFields.Where(f => !f.IsInitOnly).Should().BeEmpty(
            "per-sync state belongs on GlookoSyncContext; a mutable instance field is one DI lifetime "
            + "change away from serving one tenant's session to another tenant's sync");

        staticFields.Where(f => !f.IsInitOnly && !f.IsLiteral).Should().BeEmpty(
            "a mutable static field is shared across every tenant regardless of the DI lifetime");
    }

    // ── Test infrastructure ─────────────────────────────────────────────

    private sealed record GlookoRequestIdentity(string Host, string? Cookie, string PatientCode);

    private static async Task<SyncResult> RunSyncAsync(
        GlookoConnectorService service, Guid tenantId, GlookoConnectorConfiguration config)
    {
        await Task.Yield(); // start both runs on their own async flow before pinning the tenant
        AmbientTenant.Use(tenantId);

        var request = new SyncRequest
        {
            DataTypes = [SyncDataType.Glucose],
            From = DateTime.UtcNow.AddDays(-3), // single chunk keeps one graph request per run
        };

        return await service.SyncDataAsync(request, config, CancellationToken.None);
    }

    private static GlookoConnectorConfiguration BuildConfig(
        string email, string region, bool includeCgmBackfill = false) => new()
    {
        ConnectSource = ConnectSource.Glooko,
        Email = email,
        Password = "secret",
        Server = region,
        UseV3Api = true,
        V3IncludeCgmBackfill = includeCgmBackfill,
    };

    private static CapturingGlookoConnectorService BuildService(
        InterleavingHandler handler, ITimezoneTimelineService? timezoneTimelineService = null) =>
        new(
            new HttpClient(handler),
            new ConnectorServerResolver<GlookoConnectorConfiguration>(null, null, null),
            NullLogger<GlookoConnectorService>.Instance,
            Mock.Of<IRetryDelayStrategy>(),
            Mock.Of<IRateLimitingStrategy>(),
            new PerTenantGlookoTokenProvider(),
            timezoneTimelineService);

    /// <summary>
    /// Records the tenant each published reading was published under. The publish sink is the only
    /// place a run's resolved timezone timeline is observable from outside the service.
    /// </summary>
    private sealed class CapturingGlookoConnectorService(
        HttpClient httpClient,
        IConnectorServerResolver<GlookoConnectorConfiguration> serverResolver,
        ILogger<GlookoConnectorService> logger,
        IRetryDelayStrategy retryDelayStrategy,
        IRateLimitingStrategy rateLimitingStrategy,
        GlookoAuthTokenProvider tokenProvider,
        ITimezoneTimelineService? timezoneTimelineService)
        : GlookoConnectorService(httpClient, serverResolver, logger, retryDelayStrategy,
            rateLimitingStrategy, tokenProvider, timezoneTimelineService: timezoneTimelineService)
    {
        public ConcurrentBag<(Guid TenantId, DateTime Timestamp)> PublishedGlucose { get; } = [];

        protected override Task<bool> PublishSensorGlucoseDataAsync(
            IEnumerable<SensorGlucose> records,
            GlookoConnectorConfiguration config,
            CancellationToken cancellationToken = default)
        {
            foreach (var record in records)
                PublishedGlucose.Add((AmbientTenant.Id, record.Timestamp));
            return Task.FromResult(true);
        }
    }

    /// <summary>
    /// The tenant each run is pinned to, per async flow — standing in for the per-tenant DI scope a
    /// background sync runs in. Without it both runs would share one token-cache key and one timeline.
    /// </summary>
    private static class AmbientTenant
    {
        private static readonly AsyncLocal<Guid> Current = new();

        public static void Use(Guid tenantId) => Current.Value = tenantId;
        public static Guid Id => Current.Value;
    }

    private sealed class AmbientTenantAccessor : ITenantAccessor
    {
        public bool IsResolved => AmbientTenant.Id != Guid.Empty;
        public Guid TenantId => AmbientTenant.Id;
        public TenantContext? Context => null;
        public void SetTenant(TenantContext context) { }
    }

    /// <summary>
    /// Issues a distinct session cookie and glookoCode per tenant, so a request carrying tenant B's
    /// cookie or code is unambiguously tenant B's session.
    /// </summary>
    private sealed class PerTenantGlookoTokenProvider : GlookoAuthTokenProvider
    {
        public PerTenantGlookoTokenProvider()
            : base(
                new HttpClient(),
                new ConnectorTokenCache(),
                new ConnectorServerResolver<GlookoConnectorConfiguration>(null, null, null),
                new AmbientTenantAccessor(),
                NullLogger<GlookoAuthTokenProvider>.Instance)
        {
        }

        protected override Task<(string? Token, DateTime ExpiresAt, IReadOnlyDictionary<string, string>? Metadata)> AcquireTokenAsync(
            GlookoConnectorConfiguration config, CancellationToken cancellationToken)
        {
            var isTenantA = AmbientTenant.Id == TenantA;
            var cookie = isTenantA ? TenantACookie : TenantBCookie;
            var code = isTenantA ? TenantACode : TenantBCode;

            var userData = JsonSerializer.Serialize(
                new GlookoUserData { User = new GlookoUserLogin { GlookoCode = code } });

            return Task.FromResult<(string?, DateTime, IReadOnlyDictionary<string, string>?)>(
                (cookie, DateTime.UtcNow.AddHours(1), new Dictionary<string, string>
                {
                    ["SessionCookie"] = cookie,
                    ["UserData"] = userData,
                }));
        }
    }

    /// <summary>
    /// Hands each tenant its own timeline, as the real tenant-scoped service does.
    /// </summary>
    private sealed class AmbientTenantTimezoneTimelineService : ITimezoneTimelineService
    {
        public Task<TimezoneTimeline> GetResolverAsync(
            double? fallbackOffsetHours, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TimezoneTimeline(
            [
                new TimezoneTimelineEntry
                {
                    Timezone = AmbientTenant.Id == TenantA ? "Australia/Sydney" : "America/Toronto",
                    EffectiveFrom = DateTime.MinValue,
                },
            ], fallbackOffsetHours));

        public Task<IReadOnlyList<TimezoneTimelineEntry>> GetTimelineAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TimezoneTimelineEntry>>([]);

        public Task<bool> EnsureOriginAsync(string ianaTimezone, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<TimezoneTimelineEntry> UpsertAsync(TimezoneTimelineEntry entry, CancellationToken cancellationToken = default) =>
            Task.FromResult(entry);

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    /// <summary>
    /// Holds the first user-profile request open until one from the *other run* arrives, forcing the
    /// two runs to overlap, and records the identity every graph request was built from. Keyed on the
    /// ambient tenant — the run's own async flow, which no amount of shared service state can forge —
    /// rather than on a request count (which a run fetching its profile twice could satisfy itself) or
    /// on the session cookie (which is one of the things under test).
    /// </summary>
    private sealed class InterleavingHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _otherRunAuthenticated =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly object _gate = new();
        private Guid _parkedRun;

        public ConcurrentBag<GlookoRequestIdentity> GraphRequests { get; } = [];

        /// <summary>True when the parked run was released by the other run rather than by the timeout.</summary>
        public bool RunsOverlapped { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            var cookie = CookieOf(request);

            if (path.Contains("/api/v3/session/users", StringComparison.OrdinalIgnoreCase))
            {
                bool park;
                bool release;
                lock (_gate)
                {
                    park = _parkedRun == Guid.Empty;
                    if (park) _parkedRun = AmbientTenant.Id;
                    release = !park && _parkedRun != AmbientTenant.Id;
                }

                if (park)
                {
                    // A timeout rather than an indefinite wait, so a run that never reaches this point
                    // fails the test instead of hanging it.
                    var released = await Task.WhenAny(_otherRunAuthenticated.Task, Task.Delay(30_000));
                    RunsOverlapped = released == _otherRunAuthenticated.Task;
                }
                else if (release)
                {
                    _otherRunAuthenticated.TrySetResult();
                }

                return Json("{\"currentUser\":{\"meterUnits\":\"mgdl\",\"timezone\":\"Australia/Sydney\"}}");
            }

            if (path.Contains("/api/v3/graph/data", StringComparison.OrdinalIgnoreCase))
            {
                GraphRequests.Add(new GlookoRequestIdentity(request.RequestUri!.Host, cookie, ExtractPatient(path)));

                var x = new DateTimeOffset(FakeUtcReading).ToUnixTimeSeconds();
                return Json($"{{\"series\":{{\"cgmNormal\":[{{\"x\":{x},\"y\":120}}]}}}}");
            }

            if (path.Contains("/api/v3/users/summary/histories", StringComparison.OrdinalIgnoreCase))
                return Json("{\"histories\":[]}");

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static string? CookieOf(HttpRequestMessage request) =>
            request.Headers.TryGetValues("Cookie", out var cookies) ? string.Join(";", cookies) : null;

        private static string ExtractPatient(string pathAndQuery)
        {
            const string key = "patient=";
            var start = pathAndQuery.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return string.Empty;
            start += key.Length;
            var end = pathAndQuery.IndexOf('&', start);
            return end < 0 ? pathAndQuery[start..] : pathAndQuery[start..end];
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}

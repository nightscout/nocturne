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
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Services;

/// <summary>
/// Covers the recovery path when Glooko's <c>glookoCode</c> changes underneath a cached session:
/// patient-scoped endpoints start returning 403 <c>data_cant_view</c>, and the connector must
/// re-authenticate once to resolve the new code rather than failing or looping.
/// </summary>
public class GlookoConnectorServiceReauthTests
{
    private const string OldCode = "eu-west-1-indigo-killdeer-4650";
    private const string NewCode = "eu-west-1-blue-duke-4165";

    [Fact]
    public async Task SyncDataAsync_WhenPatientCodeChanges_ReauthenticatesAndRecovers()
    {
        // First auth resolves the stale code (403s); the re-auth resolves the new one (200s).
        var tokenProvider = new SwitchingGlookoTokenProvider([OldCode, NewCode]);
        var handler = new PatientCodeAwareHandler(forbiddenCode: OldCode);
        var service = BuildService(handler, tokenProvider);

        var request = new SyncRequest
        {
            DataTypes = [SyncDataType.Glucose],
            From = DateTime.UtcNow.AddDays(-3), // single chunk keeps the test focused
        };

        var result = await service.SyncDataAsync(request, BuildConfig(), CancellationToken.None);

        result.Success.Should().BeTrue();
        tokenProvider.AcquireCount.Should().Be(2, "the stale code should trigger exactly one re-auth");
        handler.GraphRequestedCodes.Should().Contain(NewCode, "the retry must query with the refreshed code");
    }

    [Fact]
    public async Task SyncDataAsync_WhenForbiddenPersistsAfterReauth_FailsWithoutLooping()
    {
        // Re-auth keeps resolving the same forbidden code — must give up after one retry, not loop.
        var tokenProvider = new SwitchingGlookoTokenProvider([OldCode]); // exhausted queue keeps returning OldCode
        var handler = new PatientCodeAwareHandler(forbiddenCode: OldCode);
        var service = BuildService(handler, tokenProvider);

        var request = new SyncRequest
        {
            DataTypes = [SyncDataType.Glucose],
            From = DateTime.UtcNow.AddDays(-3),
        };

        var result = await service.SyncDataAsync(request, BuildConfig(), CancellationToken.None);

        result.Success.Should().BeFalse();
        tokenProvider.AcquireCount.Should().Be(2, "it should re-auth exactly once before giving up");
    }

    /// <summary>
    /// The terminal progress message belongs to the shared run wrapper, which emits it once per
    /// run. A connector that reports its own outcome as well would hand the tenant two terminal
    /// messages, and the second would outlive the first's clear timer.
    /// </summary>
    [Theory]
    [InlineData(true, SyncPhase.Completed)]
    [InlineData(false, SyncPhase.Failed)]
    public async Task SyncDataAsync_ReportsExactlyOneTerminalMessage(
        bool codeRefreshes, SyncPhase expectedPhase)
    {
        var tokenProvider = new SwitchingGlookoTokenProvider(
            codeRefreshes ? [OldCode, NewCode] : [OldCode]);
        var handler = new PatientCodeAwareHandler(forbiddenCode: OldCode);
        var service = BuildService(handler, tokenProvider);

        var reported = new List<SyncProgressEvent>();
        var reporter = new Mock<ISyncProgressReporter>();
        reporter
            .Setup(r => r.ReportProgressAsync(It.IsAny<SyncProgressEvent>(), It.IsAny<CancellationToken>()))
            .Callback<SyncProgressEvent, CancellationToken>((e, _) => reported.Add(e))
            .Returns(Task.CompletedTask);

        var request = new SyncRequest
        {
            DataTypes = [SyncDataType.Glucose],
            From = DateTime.UtcNow.AddDays(-3),
        };

        await service.SyncDataAsync(request, BuildConfig(), CancellationToken.None, reporter.Object);

        reported.Should().NotBeEmpty(
            "a run that reported nothing at all would satisfy the count below vacuously");
        reported.Where(e => e.Phase != SyncPhase.Syncing)
            .Should().ContainSingle().Which.Phase.Should().Be(expectedPhase);
    }

    /// <summary>
    /// The V2 batch loop and the V3 histories fetch each swallowed every exception per endpoint,
    /// including the 403 the whole recovery turns on: a code that went stale mid-batch logged a
    /// warning and the run carried on querying the stale one.
    /// </summary>
    [Theory]
    [InlineData(false, GlookoConstants.CgmReadingsPath)]
    [InlineData(true, GlookoConstants.V3HistoriesPath)]
    public async Task SyncDataAsync_WhenAForbiddenEndpointIsReachedMidPass_Reauthenticates(
        bool useV3Api, string forbiddenPath)
    {
        var tokenProvider = new StaticGlookoTokenProvider();
        var handler = new GlookoEndpointHandler(forbiddenPaths: [forbiddenPath]);
        var service = GlookoSyncHarness.Service(handler, tokenProvider: tokenProvider);

        var result = await service.SyncDataAsync(
            SingleChunkRequest(SyncDataType.Glucose, SyncDataType.CarbIntake),
            GlookoSyncHarness.Config(useV3Api), CancellationToken.None);

        tokenProvider.AcquireCount.Should().Be(2, "the 403 must reach the re-authentication retry");
        handler.RequestsFor(forbiddenPath).Should().Be(2, "the retried pass must re-run the fetch");
        result.Success.Should().BeFalse("the code never refreshes, so the retried pass is forbidden too");
    }

    /// <summary>
    /// The retry re-syncs from scratch, so neither the abandoned pass's recorded fetch failure nor
    /// the records it already published may survive into the retried pass's result — the first would
    /// redden a run that went on to fetch everything, the second would count the same records twice.
    /// The 403 lands on the device settings, which are fetched after the chunks have published, so
    /// the abandoned pass has both to leave behind.
    /// </summary>
    [Fact]
    public async Task SyncDataAsync_WhenTheRetriedPassSucceeds_DropsTheAbandonedPassResults()
    {
        var tokenProvider = new StaticGlookoTokenProvider();
        var handler = new GlookoEndpointHandler(
            failingPaths: [GlookoConstants.ScheduledBasalsPath],
            forbiddenPaths: [GlookoConstants.V3DeviceSettingsPath],
            recoversAfterForbidden: true);
        var service = GlookoSyncHarness.Service(handler, tokenProvider: tokenProvider);

        var result = await service.SyncDataAsync(
            SingleChunkRequest(SyncDataType.StateSpans, SyncDataType.TempBasals, SyncDataType.Profiles),
            GlookoSyncHarness.Config(useV3Api: false), CancellationToken.None);

        tokenProvider.AcquireCount.Should().Be(2,
            "the abandoned pass's results are only abandoned if a retry actually happened");
        result.Success.Should().BeTrue();
        result.Errors.Should().BeEmpty();

        // One suspended basal and one temporary basal per chunk; one pump-mode span from that
        // suspend, plus the active-basal-program span the device settings carry.
        result.ItemsSynced[SyncDataType.TempBasals].Should().Be(2);
        result.ItemsSynced[SyncDataType.StateSpans].Should().Be(2);
    }

    // ── Test infrastructure ─────────────────────────────────────────────

    private static SyncRequest SingleChunkRequest(params SyncDataType[] dataTypes) => new()
    {
        DataTypes = [.. dataTypes],
        From = DateTime.UtcNow.AddDays(-3),
    };

    private static GlookoConnectorConfiguration BuildConfig() => new()
    {
        ConnectSource = ConnectSource.Glooko,
        Email = "user@example.com",
        Password = "secret",
        Server = GlookoConstants.RegionEU,
        UseV3Api = true,
    };

    private static GlookoConnectorService BuildService(
        PatientCodeAwareHandler handler, SwitchingGlookoTokenProvider tokenProvider) =>
        new(
            new HttpClient(handler),
            new ConnectorServerResolver<GlookoConnectorConfiguration>(null, null, null),
            NullLogger<GlookoConnectorService>.Instance,
            Mock.Of<IRetryDelayStrategy>(),
            Mock.Of<IRateLimitingStrategy>(),
            tokenProvider);

    /// <summary>
    /// Token provider that returns a session cookie plus a <c>UserData</c> metadata blob whose
    /// glookoCode advances with each acquisition, simulating a code change after re-link.
    /// </summary>
    private sealed class SwitchingGlookoTokenProvider : GlookoAuthTokenProvider
    {
        private readonly Queue<string> _codes;
        private string _lastCode = string.Empty;

        public int AcquireCount { get; private set; }

        public SwitchingGlookoTokenProvider(IEnumerable<string> codes)
            : base(
                new HttpClient(),
                new ConnectorTokenCache(),
                new ConnectorServerResolver<GlookoConnectorConfiguration>(null, null, null),
                new FakeTenantAccessor(),
                NullLogger<GlookoAuthTokenProvider>.Instance)
        {
            _codes = new Queue<string>(codes);
        }

        protected override Task<(string? Token, DateTime ExpiresAt, IReadOnlyDictionary<string, string>? Metadata)> AcquireTokenAsync(
            GlookoConnectorConfiguration config, CancellationToken cancellationToken)
        {
            AcquireCount++;
            _lastCode = _codes.Count > 0 ? _codes.Dequeue() : _lastCode;

            var userData = JsonSerializer.Serialize(
                new GlookoUserData { User = new GlookoUserLogin { GlookoCode = _lastCode } });
            var metadata = new Dictionary<string, string>
            {
                ["SessionCookie"] = "_logbook-web_session=sess",
                ["UserData"] = userData,
            };

            return Task.FromResult<(string?, DateTime, IReadOnlyDictionary<string, string>?)>(
                ("_logbook-web_session=sess", DateTime.UtcNow.AddHours(1), metadata));
        }

        private sealed class FakeTenantAccessor : Nocturne.Core.Contracts.Multitenancy.ITenantAccessor
        {
            public bool IsResolved => true;
            public Guid TenantId => Guid.Empty;
            public Nocturne.Core.Contracts.Multitenancy.TenantContext? Context => null;
            public void SetTenant(Nocturne.Core.Contracts.Multitenancy.TenantContext context) { }
        }
    }

    /// <summary>
    /// Returns 403 <c>data_cant_view</c> for graph/device requests carrying the forbidden patient
    /// code, and 200 otherwise. Records which codes the graph endpoint was queried with.
    /// </summary>
    private sealed class PatientCodeAwareHandler(string forbiddenCode) : HttpMessageHandler
    {
        public List<string> GraphRequestedCodes { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;

            if (path.Contains("/api/v3/session/users", StringComparison.OrdinalIgnoreCase))
                return Json("{\"currentUser\":{\"meterUnits\":\"mgdl\",\"timezone\":\"Australia/Sydney\"}}");

            if (path.Contains("/api/v3/users/summary/histories", StringComparison.OrdinalIgnoreCase))
                return Json("{\"histories\":[]}");

            if (path.Contains("/api/v3/graph/data", StringComparison.OrdinalIgnoreCase))
            {
                var code = ExtractPatient(path);
                GraphRequestedCodes.Add(code);
                return code == forbiddenCode ? Forbidden() : Json("{\"series\":{}}");
            }

            if (path.Contains("/api/v3/devices_and_settings", StringComparison.OrdinalIgnoreCase))
                return ExtractPatient(path) == forbiddenCode ? Forbidden() : Json("{}");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static string ExtractPatient(string pathAndQuery)
        {
            const string key = "patient=";
            var start = pathAndQuery.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return string.Empty;
            start += key.Length;
            var end = pathAndQuery.IndexOf('&', start);
            return end < 0 ? pathAndQuery[start..] : pathAndQuery[start..end];
        }

        private static Task<HttpResponseMessage> Json(string body) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });

        private static Task<HttpResponseMessage> Forbidden() =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent(
                    "{\"status\":403,\"code\":\"data_cant_view\",\"message\":\"user is not authorized to view data\"}",
                    Encoding.UTF8, "application/json"),
            });
    }
}

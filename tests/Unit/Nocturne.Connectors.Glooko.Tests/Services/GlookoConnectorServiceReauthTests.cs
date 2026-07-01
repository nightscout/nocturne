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

    // ── Test infrastructure ─────────────────────────────────────────────

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

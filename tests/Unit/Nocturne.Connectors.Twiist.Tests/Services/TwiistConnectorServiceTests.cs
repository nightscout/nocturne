using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Twiist.Configurations;
using Nocturne.Connectors.Twiist.Models;
using Nocturne.Connectors.Twiist.Services;
using Nocturne.Core.Contracts.Multitenancy;
using Xunit;

namespace Nocturne.Connectors.Twiist.Tests.Services;

public class TwiistConnectorServiceTests
{
    private const string PwdId = "7c4a6533-b8db-4cc4-823e-6ac9c99e07e5";

    [Fact]
    public async Task SyncDataAsync_AutoDiscoversSinglePatient_FetchesThatPackage()
    {
        var overviews = new List<TwiistOverview> { new() { PwdId = PwdId, PwdNickname = "Pat" } };
        var fixture = new ServiceFixture(responses: new()
        {
            [TwiistConstants.OverviewsPath] = Json(overviews),
            ["/package"] = Json(new TwiistPackage { PwdId = PwdId, Status = new TwiistStatus() })
        });

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] }, fixture.Config, CancellationToken.None);

        result.Success.Should().BeTrue();
        // The package was fetched for the auto-discovered id, not an empty path.
        fixture.RequestedPaths.Should().Contain(p => p.Contains($"/pwd/{PwdId}/package"));
    }

    [Fact]
    public async Task SyncDataAsync_NoFollowedPatients_ReportsUnhealthyWithGuidance()
    {
        var fixture = new ServiceFixture(responses: new()
        {
            [TwiistConstants.OverviewsPath] = Json(new List<TwiistOverview>())
        });

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] }, fixture.Config, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Contain("not following anyone");
    }

    [Fact]
    public async Task SyncDataAsync_MultipleFollowedPatients_ReportsUnhealthyWithNames()
    {
        var overviews = new List<TwiistOverview>
        {
            new() { PwdId = PwdId, PwdNickname = "Pat" },
            new() { PwdId = "11111111-2222-3333-4444-555555555555", PwdNickname = "Sam" }
        };
        var fixture = new ServiceFixture(responses: new()
        {
            [TwiistConstants.OverviewsPath] = Json(overviews)
        });

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] }, fixture.Config, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle()
            .Which.Should().Contain("Pat").And.Contain("Sam");
    }

    [Fact]
    public async Task SyncDataAsync_ConfiguredPatientId_SkipsOverviewDiscovery()
    {
        var fixture = new ServiceFixture(
            responses: new()
            {
                ["/package"] = Json(new TwiistPackage { PwdId = PwdId, Status = new TwiistStatus() })
            },
            patientId: PwdId);

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] }, fixture.Config, CancellationToken.None);

        result.Success.Should().BeTrue();
        fixture.RequestedPaths.Should().NotContain(p => p.Contains(TwiistConstants.OverviewsPath));
        fixture.RequestedPaths.Should().Contain(p => p.Contains($"/pwd/{PwdId}/package"));
    }

    [Fact]
    public async Task SyncDataAsync_PackageNotFound_ReportsUnhealthy()
    {
        var overviews = new List<TwiistOverview> { new() { PwdId = PwdId, PwdNickname = "Pat" } };
        var fixture = new ServiceFixture(responses: new()
        {
            [TwiistConstants.OverviewsPath] = Json(overviews),
            ["/package"] = new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{\"message\":\"Validation failed (uuid v 4 is expected)\"}")
            }
        });

        var result = await fixture.Service.SyncDataAsync(
            new SyncRequest { DataTypes = [SyncDataType.Glucose] }, fixture.Config, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Contain("no data");
    }

    private static HttpResponseMessage Json<T>(T content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(content), Encoding.UTF8, "application/json")
    };

    private sealed class FakeTwiistAuthTokenProvider(string? token) : TwiistAuthTokenProvider(
        new HttpClient(),
        new ConnectorTokenCache(),
        new ConnectorServerResolver<TwiistConnectorConfiguration>(null, null, null),
        new FakeTenantAccessor(),
        NullLogger<TwiistAuthTokenProvider>.Instance,
        Mock.Of<IRetryDelayStrategy>())
    {
        protected override Task<(string? Token, DateTime ExpiresAt, IReadOnlyDictionary<string, string>? Metadata)> AcquireTokenAsync(
            TwiistConnectorConfiguration config, CancellationToken cancellationToken) =>
            Task.FromResult<(string?, DateTime, IReadOnlyDictionary<string, string>?)>(
                token is null ? (null, DateTime.MinValue, null) : (token, DateTime.UtcNow.AddHours(1), null));

        private sealed class FakeTenantAccessor : ITenantAccessor
        {
            public bool IsResolved => true;
            public Guid TenantId => Guid.Empty;
            public TenantContext? Context => null;
            public void SetTenant(TenantContext context) { }
        }
    }

    private sealed class CapturingHandler(Dictionary<string, HttpResponseMessage> responses, List<string> requested)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            requested.Add(path);
            foreach (var (key, response) in responses)
                if (path.Contains(key, StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(response);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class ServiceFixture
    {
        public TwiistConnectorService Service { get; }
        public TwiistConnectorConfiguration Config { get; }
        public List<string> RequestedPaths { get; } = [];

        public ServiceFixture(
            Dictionary<string, HttpResponseMessage> responses,
            string? token = "valid-token",
            string patientId = "")
        {
            Config = new TwiistConnectorConfiguration
            {
                Username = "follower@example.com",
                Password = "pw",
                PatientId = patientId
            };

            var httpClient = new HttpClient(new CapturingHandler(responses, RequestedPaths));
            var publisher = new Mock<IConnectorPublisher>();
            publisher.Setup(p => p.IsAvailable).Returns(true);

            Service = new TwiistConnectorService(
                httpClient,
                new ConnectorServerResolver<TwiistConnectorConfiguration>(null, null, null),
                NullLogger<TwiistConnectorService>.Instance,
                Mock.Of<IRetryDelayStrategy>(),
                Mock.Of<IRateLimitingStrategy>(),
                new FakeTwiistAuthTokenProvider(token),
                publisher.Object);
        }
    }
}

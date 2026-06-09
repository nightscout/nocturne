using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.MyLife.Configurations;
using Nocturne.Connectors.MyLife.Mappers;
using Nocturne.Connectors.MyLife.Services;
using Nocturne.Core.Contracts.Multitenancy;
using Xunit;

namespace Nocturne.Connectors.MyLife.Tests.Services;

/// <summary>
/// Regression tests for <see cref="MyLifeConnectorService"/> authentication wiring. The connector
/// must establish its SOAP session by invoking the token provider during sync. A refactor previously
/// left that call out — <c>AuthenticateAsync</c> became a no-op and <c>PerformSyncInternalAsync</c>
/// only validated the session — so the session cache was never populated and every sync failed with
/// "session not established" regardless of credentials.
/// </summary>
public class MyLifeConnectorServiceTests
{
    private const string ServiceUrl = "https://svc.example";

    [Fact]
    public async Task SyncDataAsync_EstablishesSession_WithValidCredentials()
    {
        var tenantId = Guid.NewGuid();
        using var http = new HttpClient(new SoapStubHandler(loginSucceeds: true));
        var (service, sessionCache) = BuildService(http, tenantId);

        var config = new MyLifeConnectorConfiguration
        {
            Username = "user@example.com",
            Password = "secret",
            PatientId = "patient-1"
        };
        var request = new SyncRequest { DataTypes = [SyncDataType.Glucose] };

        var result = await service.SyncDataAsync(request, config, CancellationToken.None);

        result.Success.Should().BeTrue("a valid login must establish the session and let the sync run");
        var session = sessionCache.Get(tenantId);
        session.Should().NotBeNull("the connector must populate the session cache via the token provider");
        session!.AuthToken.Should().Be("tok-123");
        session.ServiceUrl.Should().Be(ServiceUrl);
        session.PatientId.Should().Be("patient-1");
    }

    [Fact]
    public async Task SyncDataAsync_ReportsAuthFailure_WhenLoginFails()
    {
        var tenantId = Guid.NewGuid();
        using var http = new HttpClient(new SoapStubHandler(loginSucceeds: false));
        var (service, sessionCache) = BuildService(http, tenantId);

        var config = new MyLifeConnectorConfiguration
        {
            Username = "user@example.com",
            Password = "wrong",
            PatientId = "patient-1"
        };
        var request = new SyncRequest { DataTypes = [SyncDataType.Glucose] };

        var result = await service.SyncDataAsync(request, config, CancellationToken.None);

        result.Success.Should().BeFalse("a failed login must surface as an unhealthy sync");
        result.Errors.Should().Contain(e => e.Contains("auth", StringComparison.OrdinalIgnoreCase));
        sessionCache.Get(tenantId).Should().BeNull("no session must be cached when login fails");
    }

    private static (MyLifeConnectorService Service, IMyLifeSessionCache SessionCache) BuildService(
        HttpClient http, Guid tenantId)
    {
        var resolver = new ConnectorServerResolver<MyLifeConnectorConfiguration>(null, null, null);

        var tenantAccessor = new Mock<ITenantAccessor>();
        tenantAccessor.Setup(t => t.IsResolved).Returns(true);
        tenantAccessor.Setup(t => t.TenantId).Returns(tenantId);

        var soapClient = new MyLifeSoapClient(http, NullLogger<MyLifeSoapClient>.Instance);
        var sessionCache = new MyLifeSessionCache();
        var tokenProvider = new MyLifeAuthTokenProvider(
            http,
            new ConnectorTokenCache(),
            resolver,
            tenantAccessor.Object,
            soapClient,
            sessionCache,
            NullLogger<MyLifeAuthTokenProvider>.Instance);
        var syncService = new MyLifeSyncService(soapClient, NullLogger<MyLifeSyncService>.Instance);

        var service = new MyLifeConnectorService(
            http,
            resolver,
            NullLogger<MyLifeConnectorService>.Instance,
            tokenProvider,
            new MyLifeEventProcessor(),
            sessionCache,
            tenantAccessor.Object,
            syncService,
            publisher: null);

        return (service, sessionCache);
    }

    /// <summary>
    /// Stubs the MyLife SOAP endpoints, routing by SOAPAction. Returns a valid location, login, and
    /// single-patient list; events and pump-settings return no result element so the sync completes
    /// with no data (and never reaches the archive-decryption path).
    /// </summary>
    private sealed class SoapStubHandler(bool loginSucceeds) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var action = request.Headers.TryGetValues("SOAPAction", out var values)
                ? values.FirstOrDefault() ?? string.Empty
                : string.Empty;

            var (status, body) = Respond(action);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/xml")
            });
        }

        private (HttpStatusCode Status, string Body) Respond(string action)
        {
            if (action.Contains("GetUser20"))
                return (HttpStatusCode.OK, Envelope("GetUser20Result",
                    $"{{\"Country20\":{{\"ServiceUrl\":\"{ServiceUrl}\",\"RestServiceUrl\":\"https://rest.example\"}}}}"));

            if (action.Contains("SyncPatientList"))
                return (HttpStatusCode.OK, Envelope("SyncPatientListResult",
                    "[{\"OnlinePatientId\":\"patient-1\",\"EmailNewPatient\":\"user@example.com\"}]"));

            if (action.Contains("Login"))
                return loginSucceeds
                    ? (HttpStatusCode.OK, Envelope("LoginResult", "{\"UserId\":\"user-1\",\"AuthToken\":\"tok-123\"}"))
                    : (HttpStatusCode.Unauthorized, string.Empty);

            // SyncEvents / SyncPumpSettings: no result element → treated as "no data".
            return (HttpStatusCode.OK,
                "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body/></s:Envelope>");
        }

        private static string Envelope(string element, string innerJson) =>
            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body>"
            + $"<{element}>{innerJson}</{element}></s:Body></s:Envelope>";
    }
}

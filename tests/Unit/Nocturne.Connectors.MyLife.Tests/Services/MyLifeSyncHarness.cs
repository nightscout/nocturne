using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.MyLife.Configurations;
using Nocturne.Connectors.MyLife.Mappers;
using Nocturne.Connectors.MyLife.Services;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.Connectors.MyLife.Tests.Services;

/// <summary>
/// A MyLife connector wired to stubbed SOAP endpoints, so a test drives the real sync flow from an
/// established session onwards.
/// </summary>
internal static class MyLifeSyncHarness
{
    public const string ServiceUrl = "https://svc.example";

    public static (MyLifeConnectorService Service, IMyLifeSessionCache SessionCache,
        MyLifeAuthTokenProvider TokenProvider) BuildService(
        HttpClient http,
        Guid tenantId,
        Func<MyLifeSoapClient, MyLifeSyncService>? syncServiceFactory = null,
        IConnectorPublisher? publisher = null)
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
        var syncService = syncServiceFactory?.Invoke(soapClient)
            ?? new MyLifeSyncService(soapClient, NullLogger<MyLifeSyncService>.Instance);

        var service = new MyLifeConnectorService(
            http,
            resolver,
            NullLogger<MyLifeConnectorService>.Instance,
            tokenProvider,
            new MyLifeEventProcessor(),
            sessionCache,
            tenantAccessor.Object,
            syncService,
            publisher);

        return (service, sessionCache, tokenProvider);
    }
}

/// <summary>
/// Stubs the MyLife SOAP endpoints, routing by SOAPAction. Returns a valid location, login, and
/// single-patient list; events and pump-settings return no result element so the sync completes
/// with no data (and never reaches the archive-decryption path).
/// </summary>
internal sealed class SoapStubHandler(
    bool loginSucceeds, HttpStatusCode syncStatus = HttpStatusCode.OK) : HttpMessageHandler
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
                $"{{\"Country20\":{{\"ServiceUrl\":\"{MyLifeSyncHarness.ServiceUrl}\",\"RestServiceUrl\":\"https://rest.example\"}}}}"));

        if (action.Contains("SyncPatientList"))
            return (HttpStatusCode.OK, Envelope("SyncPatientListResult",
                "[{\"OnlinePatientId\":\"patient-1\",\"EmailNewPatient\":\"user@example.com\"}]"));

        if (action.Contains("Login"))
            return loginSucceeds
                ? (HttpStatusCode.OK, Envelope("LoginResult", "{\"UserId\":\"user-1\",\"AuthToken\":\"tok-123\"}"))
                : (HttpStatusCode.Unauthorized, string.Empty);

        // SyncEvents / SyncPumpSettings: no result element → treated as "no data".
        return (syncStatus,
            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body/></s:Envelope>");
    }

    private static string Envelope(string element, string innerJson) =>
        "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body>"
        + $"<{element}>{innerJson}</{element}></s:Body></s:Envelope>";
}

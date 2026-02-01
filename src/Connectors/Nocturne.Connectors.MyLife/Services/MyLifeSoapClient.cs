using System.Security;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.MyLife.Configurations.Constants;
using Nocturne.Connectors.MyLife.Models;

namespace Nocturne.Connectors.MyLife.Services;

public class MyLifeSoapClient(HttpClient httpClient, ILogger<MyLifeSoapClient> logger)
{
    public async Task<MyLifeLocation?> GetUserLocationAsync(string login, CancellationToken cancellationToken)
    {
        var body =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\">" +
            "<s:Body>" +
            "<GetUser20 xmlns=\"http://tempuri.org/\">" +
            $"<login>{SecurityElement.Escape(login)}</login>" +
            "</GetUser20>" +
            "</s:Body>" +
            "</s:Envelope>";

        var response = await PostSoapAsync(
            MyLifeConstants.UserLocationServiceUrl,
            MyLifeConstants.SoapActions.GetUser20,
            body,
            cancellationToken
        );

        if (string.IsNullOrWhiteSpace(response)) return null;

        var payload = ExtractSoapResult(response, "GetUser20Result");
        if (string.IsNullOrWhiteSpace(payload)) return null;

        return JsonSerializer.Deserialize<MyLifeLocation>(payload);
    }

    public async Task<MyLifeLoginResult?> LoginAsync(
        string serviceUrl,
        int appPlatform,
        int appVersion,
        string user,
        string password,
        CancellationToken cancellationToken)
    {
        var body =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\">" +
            "<s:Body>" +
            "<Login xmlns=\"http://tempuri.org/\">" +
            $"<appPlatform>{appPlatform}</appPlatform>" +
            $"<appVersion>{appVersion}</appVersion>" +
            $"<user>{SecurityElement.Escape(user)}</user>" +
            $"<password>{SecurityElement.Escape(password)}</password>" +
            "</Login>" +
            "</s:Body>" +
            "</s:Envelope>";

        var url = CombineUrl(serviceUrl, MyLifeConstants.ServicePaths.AuthService);

        var response = await PostSoapAsync(
            url,
            MyLifeConstants.SoapActions.Login,
            body,
            cancellationToken
        );

        if (string.IsNullOrWhiteSpace(response)) return null;

        var payload = ExtractSoapResult(response, "LoginResult");
        return string.IsNullOrWhiteSpace(payload) ? null : JsonSerializer.Deserialize<MyLifeLoginResult>(payload);
    }

    public async Task<IReadOnlyList<MyLifePatient>> SyncPatientListAsync(
        string serviceUrl,
        string authToken,
        CancellationToken cancellationToken)
    {
        var body =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\">" +
            "<s:Body>" +
            "<SyncPatientList xmlns=\"http://tempuri.org/\">" +
            "<patientsToDelete xmlns:d4p1=\"http://schemas.microsoft.com/2003/10/Serialization/Arrays\" xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\" />" +
            "<patientIds xmlns:d4p1=\"http://schemas.microsoft.com/2003/10/Serialization/Arrays\" xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\" />" +
            $"<authToken>{SecurityElement.Escape(authToken)}</authToken>" +
            "</SyncPatientList>" +
            "</s:Body>" +
            "</s:Envelope>";

        var url = CombineUrl(serviceUrl, MyLifeConstants.ServicePaths.SyncService);

        var response = await PostSoapAsync(
            url,
            MyLifeConstants.SoapActions.SyncPatientList,
            body,
            cancellationToken
        );

        if (string.IsNullOrWhiteSpace(response)) return Array.Empty<MyLifePatient>();

        var payload = ExtractSoapResult(response, "SyncPatientListResult");
        if (string.IsNullOrWhiteSpace(payload)) return Array.Empty<MyLifePatient>();

        var result = JsonSerializer.Deserialize<List<MyLifePatient>>(payload);
        if (result == null) return Array.Empty<MyLifePatient>();
        return result;
    }

    public async Task<string?> SyncEventsAsync(
        string serviceUrl,
        string patientId,
        string authToken,
        string month,
        CancellationToken cancellationToken)
    {
        var body =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\">" +
            "<s:Body>" +
            "<SyncEvents xmlns=\"http://tempuri.org/\">" +
            $"<patientId>{SecurityElement.Escape(patientId)}</patientId>" +
            $"<month>{SecurityElement.Escape(month)}</month>" +
            $"<authToken>{SecurityElement.Escape(authToken)}</authToken>" +
            "</SyncEvents>" +
            "</s:Body>" +
            "</s:Envelope>";

        var url = CombineUrl(serviceUrl, MyLifeConstants.ServicePaths.SyncService);

        var response = await PostSoapAsync(
            url,
            MyLifeConstants.SoapActions.SyncEvents,
            body,
            cancellationToken
        );

        if (string.IsNullOrWhiteSpace(response)) return null;

        return ExtractSoapResult(response, "SyncEventsResult");
    }

    private async Task<string> PostSoapAsync(
        string url,
        string action,
        string body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("SOAPAction", action);
        request.Content = new StringContent(body, Encoding.UTF8, "text/xml");

        var response = await httpClient.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);

        if (response.IsSuccessStatusCode) return content;

        logger.LogWarning("MyLife SOAP request failed {StatusCode}", response.StatusCode);
        return string.Empty;
    }

    private string? ExtractSoapResult(string xml, string elementName)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            var element = doc.Descendants().FirstOrDefault(x => x.Name.LocalName == elementName);
            return element?.Value;
        }
        catch (XmlException ex)
        {
            logger.LogWarning(ex, "Failed to parse SOAP XML response for element {ElementName}", elementName);
            return null;
        }
    }

    private static string CombineUrl(string serviceUrl, string path)
    {
        var baseUrl = serviceUrl.TrimEnd('/');
        var suffix = path.TrimStart('/');
        return $"{baseUrl}/{suffix}";
    }
}
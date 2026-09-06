using System.Net;
using System.Text;
using Nocturne.Connectors.CareLink.Configurations;

namespace Nocturne.Connectors.CareLink.Tests.Services;

/// <summary>
/// Serves the CareLink discovery config, the Auth0 SSO config it points to, and the Auth0 token
/// endpoint, recording every request so tests can assert on method, URL, headers and body.
/// </summary>
internal sealed class CareLinkFakeHandler : HttpMessageHandler
{
    internal const string SsoConfigUrl = "https://carelink.example/configs/v1/carepartner_auth0_sso_config.json";
    internal const string LoginHost = "carelink-login.example";
    internal const string ClientId = "discovered-client-id";
    internal const string Audience = "carepartner.patient.ous";
    internal const string TokenUrl = $"https://{LoginHost}/oauth/token";

    internal sealed record RecordedRequest(HttpMethod Method, string Url, string? UserAgent, string? Body);

    internal List<RecordedRequest> Requests { get; } = [];

    internal string TokenResponseJson { get; init; } =
        """{"access_token":"new-access-token","refresh_token":"rotated-refresh-token"}""";

    /// <summary>Status served for any URL the fake does not model.</summary>
    internal HttpStatusCode UnmodelledStatus { get; init; } = HttpStatusCode.NotFound;

    /// <summary>Body for the monitor endpoint; when null that endpoint is left unmodelled like the others.</summary>
    internal string? MonitorDataJson { get; init; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new RecordedRequest(
            request.Method,
            url,
            request.Headers.UserAgent.Count > 0 ? request.Headers.UserAgent.ToString() : null,
            body));

        if (url.Contains("/discover/", StringComparison.Ordinal))
            return Json($$"""
                {"CP":[{"region":"EU","Auth0SSOConfiguration":"{{SsoConfigUrl}}"}]}
                """);

        if (url == SsoConfigUrl)
            return Json($$"""
                {
                  "server": { "hostname": "{{LoginHost}}", "port": 443, "prefix": "" },
                  "client": {
                    "client_id": "{{ClientId}}",
                    "scope": "profile openid offline_access",
                    "audience": "{{Audience}}",
                    "redirect_uri": "com.medtronic.carepartner:/sso"
                  },
                  "system_endpoints": {
                    "authorization_endpoint_path": "/authorize",
                    "token_endpoint_path": "/oauth/token"
                  }
                }
                """);

        if (url == TokenUrl)
            return Json(TokenResponseJson);

        if (MonitorDataJson is not null
            && url.EndsWith(CareLinkConstants.Endpoints.MonitorData, StringComparison.Ordinal))
            return Json(MonitorDataJson);

        return new HttpResponseMessage(UnmodelledStatus);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

using System.Text.Json.Serialization;

namespace Nocturne.Connectors.CareLink.Models;

public class Auth0SSOConfig
{
    [JsonPropertyName("server")]
    public Auth0Server Server { get; set; } = new();

    [JsonPropertyName("client")]
    public Auth0Client Client { get; set; } = new();

    [JsonPropertyName("system_endpoints")]
    public Auth0Endpoints SystemEndpoints { get; set; } = new();

    public string GetBaseUrl()
    {
        var port = Server.Port is > 0 and not 443 ? $":{Server.Port}" : "";
        var prefix = string.IsNullOrEmpty(Server.Prefix) ? "" : Server.Prefix;
        return $"https://{Server.Hostname}{port}{prefix}";
    }
}

public class Auth0Server
{
    [JsonPropertyName("hostname")]
    public string Hostname { get; set; } = string.Empty;

    [JsonPropertyName("port")]
    public int? Port { get; set; }

    [JsonPropertyName("prefix")]
    public string? Prefix { get; set; }
}

public class Auth0Client
{
    [JsonPropertyName("client_id")]
    public string ClientId { get; set; } = string.Empty;

    [JsonPropertyName("scope")]
    public string Scope { get; set; } = string.Empty;

    [JsonPropertyName("audience")]
    public string Audience { get; set; } = string.Empty;

    [JsonPropertyName("redirect_uri")]
    public string RedirectUri { get; set; } = string.Empty;
}

public class Auth0Endpoints
{
    [JsonPropertyName("authorization_endpoint_path")]
    public string AuthorizationEndpointPath { get; set; } = string.Empty;

    [JsonPropertyName("token_endpoint_path")]
    public string TokenEndpointPath { get; set; } = string.Empty;
}

public class DiscoverResponse
{
    [JsonPropertyName("CP")]
    public List<DiscoverEntry>? Cp { get; set; }
}

public class DiscoverEntry
{
    [JsonPropertyName("region")]
    public string? Region { get; set; }

    [JsonPropertyName("UseSSOConfiguration")]
    public string? UseSSOConfiguration { get; set; }

    [JsonPropertyName("Auth0SSOConfiguration")]
    public string? Auth0SSOConfiguration { get; set; }
}

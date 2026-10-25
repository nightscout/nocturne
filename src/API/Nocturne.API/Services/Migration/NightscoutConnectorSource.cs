using System.Text.Json;
using Nocturne.Core.Contracts.Connectors;

namespace Nocturne.API.Services.Migration;

/// <summary>The Nightscout URL and API secret saved with a connector's configuration.</summary>
public record NightscoutConnectorSource(string? Url, string? ApiSecret)
{
    /// <summary>The saved source for <paramref name="connectorName"/>, or null when it has no saved configuration.</summary>
    public static async Task<NightscoutConnectorSource?> ReadAsync(
        IConnectorConfigurationService configurations, string connectorName, CancellationToken ct)
    {
        var config = await configurations.GetConfigurationAsync(connectorName, ct);
        if (config is null)
            return null;

        var secrets = await configurations.GetSecretsAsync(connectorName, ct);
        var url = config.Configuration?.RootElement is { ValueKind: JsonValueKind.Object } root
            && root.TryGetProperty("url", out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        return new NightscoutConnectorSource(url, secrets.GetValueOrDefault("apiSecret"));
    }
}

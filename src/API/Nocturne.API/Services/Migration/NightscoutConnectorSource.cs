using System.Text.Json;
using Nocturne.Core.Contracts.Connectors;

namespace Nocturne.API.Services.Migration;

/// <summary>
/// The Nightscout URL, API secret and history limit saved with a connector's configuration. The
/// limit is the connector's <c>HistoryDays</c>: how many days of history to import, or 0 for all.
/// </summary>
public record NightscoutConnectorSource(string? Url, string? ApiSecret, int HistoryDays = 0)
{
    /// <summary>The oldest instant an import started at <paramref name="now"/> reads, or null for the whole history.</summary>
    public DateTime? HistoryStart(DateTime now) => HistoryDays > 0 ? now.AddDays(-HistoryDays) : null;

    /// <summary>The saved source for <paramref name="connectorName"/>, or null when it has no saved configuration.</summary>
    public static async Task<NightscoutConnectorSource?> ReadAsync(
        IConnectorConfigurationService configurations, string connectorName, CancellationToken ct)
    {
        var config = await configurations.GetConfigurationAsync(connectorName, ct);
        if (config is null)
            return null;

        var secrets = await configurations.GetSecretsAsync(connectorName, ct);
        var root = config.Configuration?.RootElement is { ValueKind: JsonValueKind.Object } saved ? saved : (JsonElement?)null;
        var url = root?.TryGetProperty("url", out var value) == true && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
        var historyDays = root?.TryGetProperty("historyDays", out var days) == true && days.TryGetInt32(out var limit)
            ? limit
            : 0;

        return new NightscoutConnectorSource(url, secrets.GetValueOrDefault("apiSecret"), historyDays);
    }
}

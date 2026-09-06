using Nocturne.Connectors.Core.Models;

namespace Nocturne.Connectors.Core.Extensions;

/// <summary>
///     The per-data-type sync toggles, resolved from the two enums that already name them: the
///     <see cref="ConnectorPropertyKey"/> called <c>Sync{X}</c> is the toggle for the
///     <see cref="SyncDataType"/> called <c>X</c>. Anything else beginning with <c>Sync</c> —
///     <see cref="ConnectorPropertyKey.SyncIntervalMinutes"/> — names no data type and is not a
///     toggle.
/// </summary>
public static class ConnectorSyncToggles
{
    private const string TogglePrefix = "Sync";

    /// <summary>
    ///     Every sync toggle property key, mapped to the data type it gates.
    /// </summary>
    public static IReadOnlyDictionary<ConnectorPropertyKey, SyncDataType> ByPropertyKey { get; } = Build();

    private static Dictionary<ConnectorPropertyKey, SyncDataType> Build()
    {
        var toggles = new Dictionary<ConnectorPropertyKey, SyncDataType>();

        foreach (var key in Enum.GetValues<ConnectorPropertyKey>())
        {
            var name = key.ToString();
            if (!name.StartsWith(TogglePrefix, StringComparison.Ordinal))
                continue;

            if (Enum.TryParse<SyncDataType>(name[TogglePrefix.Length..], out var dataType)
                && Enum.IsDefined(dataType))
                toggles[key] = dataType;
        }

        return toggles;
    }
}

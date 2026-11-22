using Nocturne.API.Services.Connectors;

namespace Nocturne.API.Services;

/// <summary>
/// The live state names of each background job table, as the startup sweep reads them.
/// </summary>
public static class BackgroundJobLiveStates
{
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ByTable { get; } =
        new Dictionary<string, IReadOnlyList<string>>
        {
            ["connector_reset_jobs"] = ConnectorResetJobStates.LiveNames,
        };
}

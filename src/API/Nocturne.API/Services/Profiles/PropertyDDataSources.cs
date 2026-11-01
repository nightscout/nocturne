using Nocturne.Core.Contracts.Legacy;

namespace Nocturne.API.Services.Profiles;

/// <summary>
/// The <see cref="DDataCollections"/> each property of <see cref="PropertiesService"/> reads, and so
/// the only ones a request for those properties must load.
/// </summary>
internal static class PropertyDDataSources
{
    private static readonly Dictionary<string, DDataCollections> Sources = new()
    {
        ["bgnow"] = DDataCollections.Sgvs,
        ["delta"] = DDataCollections.Sgvs,
        ["direction"] = DDataCollections.Sgvs,
        ["rawbg"] = DDataCollections.Sgvs,
        ["ar2"] = DDataCollections.Sgvs,
        ["iob"] = DDataCollections.Sgvs | DDataCollections.Treatments,
        ["cob"] = DDataCollections.Sgvs | DDataCollections.Treatments,
        ["bwp"] = DDataCollections.Sgvs | DDataCollections.Treatments | DDataCollections.Profiles,
        ["basal"] = DDataCollections.Treatments | DDataCollections.Profiles,
        ["profile"] = DDataCollections.Profiles,
        ["devicestatus"] = DDataCollections.DeviceStatus,
        ["upbat"] = DDataCollections.DeviceStatus,
        ["pump"] = DDataCollections.DeviceStatus | DDataCollections.Treatments,
        ["loop"] = DDataCollections.DeviceStatus | DDataCollections.Treatments,
        ["openaps"] = DDataCollections.DeviceStatus | DDataCollections.Treatments,
        ["dbsize"] = DDataCollections.DbStats,
        ["cage"] = DDataCollections.None,
        ["sage"] = DDataCollections.None,
        ["iage"] = DDataCollections.None,
        ["bage"] = DDataCollections.None,
        ["runtimestate"] = DDataCollections.None,
    };

    /// <summary>
    /// The collections <paramref name="requested"/> needs; everything when null or when a name is
    /// not mapped, so a property added without an entry reads full data.
    /// </summary>
    internal static DDataCollections For(IReadOnlyCollection<string>? requested)
    {
        if (requested is null)
            return DDataCollections.All;

        var needed = DDataCollections.None;
        foreach (var name in requested)
            needed |= Sources.GetValueOrDefault(name, DDataCollections.All);
        return needed;
    }
}

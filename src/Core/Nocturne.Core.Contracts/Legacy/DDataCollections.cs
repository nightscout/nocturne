namespace Nocturne.Core.Contracts.Legacy;

/// <summary>
/// The collections of a <see cref="Nocturne.Core.Models.DData"/> that <see cref="IDDataService"/> loads.
/// A collection left out keeps its empty default.
/// </summary>
[Flags]
public enum DDataCollections
{
    None = 0,
    Sgvs = 1 << 0,
    Treatments = 1 << 1,
    Mbgs = 1 << 2,
    Cals = 1 << 3,
    Profiles = 1 << 4,
    DeviceStatus = 1 << 5,
    Food = 1 << 6,
    Activity = 1 << 7,
    DbStats = 1 << 8,
    All = Sgvs | Treatments | Mbgs | Cals | Profiles | DeviceStatus | Food | Activity | DbStats,
}

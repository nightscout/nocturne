using Nocturne.Core.Constants;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>The v3 settings collection within the settings table.</summary>
public static class SettingsQueryExtensions
{
    /// <summary>The rows of the v3 collection: every one whose key is not reserved (<see cref="SettingsKeys"/>).</summary>
    public static IQueryable<SettingsEntity> V3Documents(this IQueryable<SettingsEntity> settings) =>
        settings.Where(s =>
            !s.Key.StartsWith(SettingsKeys.UiSettingsPrefix) && !s.Key.StartsWith(SettingsKeys.ConnectorsPrefix));
}

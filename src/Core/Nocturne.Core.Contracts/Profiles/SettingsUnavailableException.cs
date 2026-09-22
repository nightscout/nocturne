namespace Nocturne.Core.Contracts.Profiles;

/// <summary>
/// A settings read failed, so the tenant's own configuration is unknown.
/// </summary>
/// <remarks>
/// Settings services reserve null for a read that failed, because a tenant that has saved nothing
/// reads back defaults and the two answers are not interchangeable: the defaults turn features on,
/// set a sleep schedule and set a match window that the tenant may have chosen against. A caller
/// whose own contract has no room for the distinction — one whose null already means "no such
/// thing" — raises this instead of running on the defaults.
/// </remarks>
public sealed class SettingsUnavailableException : Exception
{
    /// <param name="settings">Which settings could not be read, for the log line.</param>
    public SettingsUnavailableException(string settings)
        : base($"{settings} could not be read, so the tenant's configuration is unknown.")
    {
        Settings = settings;
    }

    /// <summary>Which settings could not be read.</summary>
    public string Settings { get; }
}

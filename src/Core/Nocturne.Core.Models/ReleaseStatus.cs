using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// The running release, the newest published release, and when an automatic updater would
/// install it.
/// </summary>
public class ReleaseStatusDto
{
    public string CurrentVersion { get; set; } = string.Empty;

    /// <summary>
    /// False for a build not stamped from a release tag (a <c>develop</c> image or a local build),
    /// which is never compared with published releases.
    /// </summary>
    public bool IsReleaseBuild { get; set; }

    public bool UpdateAvailable { get; set; }

    /// <summary>
    /// The published release matching <see cref="CurrentVersion"/>, or null when there is none or
    /// GitHub could not be reached.
    /// </summary>
    public ReleaseNotesDto? Current { get; set; }

    /// <summary>
    /// The newest published release, set only when it is newer than <see cref="CurrentVersion"/>.
    /// </summary>
    public ReleaseNotesDto? Latest { get; set; }

    public AutoUpdateStatusDto AutoUpdate { get; set; } = new();
}

/// <summary>
/// One published release.
/// </summary>
public class ReleaseNotesDto
{
    public string Version { get; set; } = string.Empty;

    public string? Name { get; set; }

    public string Url { get; set; } = string.Empty;

    public DateTimeOffset? PublishedAt { get; set; }

    /// <summary>
    /// The release notes as GitHub rendered them, passed through the HTML sanitizer.
    /// </summary>
    public string NotesHtml { get; set; } = string.Empty;
}

/// <summary>
/// What the deployment's Watchtower container will do with a new release.
/// </summary>
public class AutoUpdateStatusDto
{
    public AutoUpdateMode Mode { get; set; }

    /// <summary>
    /// When Watchtower last checked for new images, or null before its first check or when its
    /// HTTP API is not reachable.
    /// </summary>
    public DateTimeOffset? LastCheckAt { get; set; }

    /// <summary>
    /// <see cref="LastCheckAt"/> plus <see cref="CheckIntervalSeconds"/>: when a release already
    /// published will be installed. Null when either is unknown.
    /// </summary>
    public DateTimeOffset? NextCheckAt { get; set; }

    public int? CheckIntervalSeconds { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter<AutoUpdateMode>))]
public enum AutoUpdateMode
{
    /// <summary>
    /// No Watchtower container answers on the deployment's network: updates are installed by hand.
    /// </summary>
    NotDetected,

    /// <summary>
    /// Watchtower is running, but its HTTP API is off or refused the token, so its mode and
    /// schedule are unknown.
    /// </summary>
    Detected,

    /// <summary>
    /// Watchtower pulls and restarts updated containers.
    /// </summary>
    Automatic,

    /// <summary>
    /// Watchtower only reports new images and installs nothing.
    /// </summary>
    MonitorOnly,
}

namespace Nocturne.Core.Models.V4;

/// <summary>
/// The profile activations at one moment, as the profile page shows them: what applies, what an AID
/// system reports, and the switch a person recorded in Nocturne.
/// </summary>
public sealed class ProfileActivationStatus
{
    /// <summary>The activation that applies; null when the profile store's default does.</summary>
    public ProfileActivationDto? Active { get; set; }

    /// <summary>The effective activation an AID system reports its pump runs, when one is in force.</summary>
    public ProfileActivationDto? Reported { get; set; }

    /// <summary>The newest switch recorded in Nocturne that is running, whether or not it applies.</summary>
    public ProfileActivationDto? Recorded { get; set; }

    /// <summary>
    /// Whether a switch recorded in Nocturne applies now: false while an AID system reports what its
    /// pump runs.
    /// </summary>
    public bool RecordedSwitchesApply { get; set; }

    /// <summary>
    /// Whether the stored profile <see cref="Recorded"/> names has schedules other than the ones it was
    /// recorded with. The switch keeps running those; switching again takes up the current ones.
    /// </summary>
    public bool RecordedProfileChanged { get; set; }
}

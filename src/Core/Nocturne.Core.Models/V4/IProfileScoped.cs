namespace Nocturne.Core.Models.V4;

/// <summary>
/// A record that belongs to one named profile store.
/// </summary>
/// <remarks>
/// Implemented by the five record types a legacy <see cref="Profile"/> upload decomposes into, one
/// set per store in <see cref="Profile.Store"/>.
/// </remarks>
/// <seealso cref="TherapySettings"/>
/// <seealso cref="BasalSchedule"/>
/// <seealso cref="CarbRatioSchedule"/>
/// <seealso cref="SensitivitySchedule"/>
/// <seealso cref="TargetRangeSchedule"/>
public interface IProfileScoped
{
    /// <summary>Key of the named profile store this record belongs to.</summary>
    string ProfileName { get; }
}

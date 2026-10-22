using System.Text.Json;
using Nocturne.API.Services.V4;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.ProfileActivations;

/// <summary>
/// Builds a <see cref="ProfileActivationSnapshot"/> from the <c>profileJson</c> a switch was uploaded
/// with, normalised as <see cref="ProfileDecomposer"/> normalises a stored profile.
/// </summary>
/// <remarks>
/// Targets go through <see cref="ProfileDecomposer.MergeTargets"/>, which undoes the extra conversion a
/// mmol <c>profileJson</c> carrying mg/dL values (a client's copy of a profile read back before legacy
/// reads converted it to mmol) would take, so re-sending a switch cannot rebuild targets migration
/// <c>RepairDoubleConvertedMmolProfiles</c> repaired. Its ISF is converted as uploaded: a correct mmol ISF
/// can convert to the same numbers. Migration <c>AddProfileActivations</c> built these snapshots in SQL
/// without that undo; <c>RepairDoubleConvertedMmolProfiles</c> applies it to what it built.
/// </remarks>
internal static class ProfileActivationSnapshots
{
    /// <returns>
    /// The snapshot, or one carrying only <paramref name="insulin"/> when <paramref name="profileJson"/>
    /// is absent or malformed, or null when there is neither.
    /// </returns>
    public static ProfileActivationSnapshot? From(string? profileJson, TreatmentInsulinContext? insulin)
    {
        var snapshot = Parse(profileJson);
        if (snapshot is null && insulin is null)
            return null;

        snapshot ??= new ProfileActivationSnapshot();
        snapshot.Insulin = insulin;
        return snapshot;
    }

    /// <summary>Whether the two snapshots hold different basal, carb ratio, sensitivity or target schedules.</summary>
    public static bool SchedulesDiffer(ProfileActivationSnapshot a, ProfileActivationSnapshot b) =>
        !SameEntries(a.Basal, b.Basal)
        || !SameEntries(a.CarbRatio, b.CarbRatio)
        || !SameEntries(a.Sensitivity, b.Sensitivity)
        || !(a.Targets ?? []).Select(t => (t.Time, t.Low, t.High)).SequenceEqual((b.Targets ?? []).Select(t => (t.Time, t.Low, t.High)));

    private static bool SameEntries(List<ScheduleEntry>? a, List<ScheduleEntry>? b) =>
        (a ?? []).Select(e => (e.Time, e.Value)).SequenceEqual((b ?? []).Select(e => (e.Time, e.Value)));

    private static ProfileActivationSnapshot? Parse(string? profileJson)
    {
        if (string.IsNullOrWhiteSpace(profileJson))
            return null;

        ProfileData? data;
        try
        {
            data = JsonSerializer.Deserialize<ProfileData>(profileJson);
        }
        catch (JsonException)
        {
            return null;
        }

        if (data is null)
            return null;

        List<TargetRangeEntry> targets;
        try
        {
            targets = ProfileDecomposer.MergeTargets(data.TargetLow ?? [], data.TargetHigh ?? [], data.Units);
        }
        catch (ArgumentException)
        {
            // Two high targets at one time; the merge cannot pick between them.
            targets = [];
        }

        return new ProfileActivationSnapshot
        {
            Basal = ProfileDecomposer.ConvertTimeValues(data.Basal ?? []),
            CarbRatio = ProfileDecomposer.ConvertTimeValues(data.CarbRatio ?? []),
            Sensitivity = ProfileDecomposer.ConvertSensitivityValues(data.Sens ?? [], data.Units),
            Targets = targets,
            Units = data.Units,
            Dia = data.Dia,
            CarbsHr = data.CarbsHr,
            Timezone = data.Timezone,
        };
    }
}

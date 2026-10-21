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
/// Migration <c>AddProfileActivations</c> builds the same snapshot in SQL for the Profile spans it
/// converts; a change here is a change there.
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

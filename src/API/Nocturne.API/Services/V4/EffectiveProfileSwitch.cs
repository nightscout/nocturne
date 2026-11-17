using Nocturne.Core.Models;

namespace Nocturne.API.Services.V4;

/// <summary>
/// AAPS's EffectiveProfileSwitch: the profile the pump is actually running, recorded once the pump
/// accepts a Profile Switch and again when a temporary one runs out. Nightscout has no collection
/// for it, so AAPS uploads it as a <c>Note</c> treatment whose <c>notes</c> is the customized
/// profile name, carrying the effective <c>profileJson</c> and the <c>original*</c> fields of the
/// switch that produced it (<c>EffectiveProfileSwitchExtension.kt</c> in AAPS plugins/sync). AAPS
/// itself tells it apart from a plain note by <c>eventType == "Note"</c> plus an
/// <c>originalProfileName</c> (<c>TreatmentMapper.kt</c> in AAPS core/nssdk), and so does this.
/// </summary>
/// <remarks>
/// It decomposes into an effective <see cref="Core.Models.V4.ProfileActivation"/> rather than a
/// Note, read back as the Note document it was uploaded as (<see cref="Treatments.LegacyProfileActivationTable"/>).
/// The activation names the base profile with its percentage and timeshift, as a Profile Switch's
/// does, and keeps the effective <c>profileJson</c>, which already has them applied, as its snapshot.
/// </remarks>
internal static class EffectiveProfileSwitch
{
    internal const string NoteEventType = "Note";

    /// <summary>Whether <paramref name="treatment"/> is an AAPS effective profile switch.</summary>
    internal static bool Is(Treatment treatment) =>
        string.Equals(treatment.EventType?.Trim(), NoteEventType, StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrEmpty(treatment.OriginalProfileName);

    /// <summary>
    /// The span metadata of an effective profile switch, which
    /// <see cref="ProfileActivations.ProfileActivationLegacyFields.FromSpanMetadata"/> converts: the
    /// activation's fields, and every uploaded field the Note document is read back with.
    /// </summary>
    internal static Dictionary<string, object> Metadata(Treatment treatment)
    {
        var metadata = new Dictionary<string, object>
        {
            [ProfileSpanMetadata.EventTypeKey] = treatment.EventType!,
            [ProfileSpanMetadata.ProfileNameKey] = treatment.OriginalCustomizedName ?? treatment.OriginalProfileName!,
            [ProfileSpanMetadata.OriginalProfileNameKey] = treatment.OriginalProfileName!,
        };

        if (!string.IsNullOrEmpty(treatment.OriginalCustomizedName))
            metadata[ProfileSpanMetadata.OriginalCustomizedNameKey] = treatment.OriginalCustomizedName;

        if (treatment.OriginalPercentage is { } percentage)
        {
            metadata[ProfileSpanMetadata.OriginalPercentageKey] = percentage;
            metadata[ProfileSpanMetadata.PercentageKey] = percentage;
        }

        if (treatment.OriginalTimeshift is { } timeshiftMs)
        {
            metadata[ProfileSpanMetadata.OriginalTimeshiftKey] = timeshiftMs;
            metadata[ProfileSpanMetadata.TimeshiftKey] =
                ProfileSpanMetadata.ToHours(timeshiftMs, ProfileTimeshiftUnit.Milliseconds);
        }

        if (treatment.OriginalDuration is { } duration)
            metadata[ProfileSpanMetadata.OriginalDurationKey] = duration;

        if (treatment.OriginalEnd is { } end)
            metadata[ProfileSpanMetadata.OriginalEndKey] = end;

        if (!string.IsNullOrEmpty(treatment.ProfileJson))
            metadata[ProfileSpanMetadata.ProfileJsonKey] = treatment.ProfileJson;

        return metadata;
    }

    /// <summary>The Note document an effective profile switch was uploaded as.</summary>
    /// <remarks>
    /// AAPS drops an effective switch read back without any one of its <c>original*</c> fields
    /// (<c>TreatmentMapper.kt</c> in AAPS core/nssdk), so each is served, defaulted to an unchanged
    /// permanent switch of the named profile when the upload left it out.
    /// </remarks>
    internal static Treatment Project(ProfileSpanMetadata span, long startMills) =>
        new()
        {
            EventType = span.EventType,
            ProfileJson = span.ProfileJson,
            OriginalProfileName = span.OriginalProfileName,
            OriginalCustomizedName = span.OriginalCustomizedName ?? span.OriginalProfileName,
            OriginalPercentage = span.OriginalPercentage ?? 100,
            OriginalTimeshift = (int)(span.OriginalTimeshift ?? 0),
            OriginalDuration = (int)(span.OriginalDuration ?? 0),
            OriginalEnd = span.OriginalEnd ?? startMills,
        };
}

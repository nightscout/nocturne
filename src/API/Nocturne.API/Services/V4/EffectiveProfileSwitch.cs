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
/// It decomposes into a Profile span rather than a Note, and that span is read back as the Note
/// document it was uploaded as (<see cref="LegacyTreatmentTables.StateSpanTables"/>). The span names
/// the base profile with its percentage and timeshift, as a Profile Switch span does, since its
/// <c>profileJson</c> already has them applied. It ends whatever span of its source was still
/// running when it took effect, so the Profile Switch it confirms and the effective switch it
/// becomes do not both stand over the same time.
/// </remarks>
internal static class EffectiveProfileSwitch
{
    internal const string NoteEventType = "Note";

    internal const string OriginalProfileNameKey = "originalProfileName";
    internal const string OriginalCustomizedNameKey = "originalCustomizedName";
    internal const string OriginalTimeshiftKey = "originalTimeshift";
    internal const string OriginalPercentageKey = "originalPercentage";
    internal const string OriginalDurationKey = "originalDuration";
    internal const string OriginalEndKey = "originalEnd";

    private const double MillisPerHour = 3_600_000;

    /// <summary>Whether <paramref name="treatment"/> is an AAPS effective profile switch.</summary>
    internal static bool Is(Treatment treatment) =>
        string.Equals(treatment.EventType?.Trim(), NoteEventType, StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrEmpty(treatment.OriginalProfileName);

    /// <summary>Whether a Profile span's metadata is that of an effective profile switch.</summary>
    internal static bool IsSpan(IDictionary<string, object>? metadata) =>
        metadata.TryReadString(LegacyTreatmentTables.UploadedEventTypeKey) is not null;

    /// <summary>
    /// The span metadata of an effective profile switch: what the active-profile resolver reads, and
    /// every uploaded field the Note document is read back with.
    /// </summary>
    internal static Dictionary<string, object> Metadata(Treatment treatment)
    {
        var metadata = new Dictionary<string, object>
        {
            [LegacyTreatmentTables.UploadedEventTypeKey] = treatment.EventType!,
            [StateSpanMetadataExtensions.ProfileNameKey] = treatment.OriginalCustomizedName ?? treatment.OriginalProfileName!,
            [OriginalProfileNameKey] = treatment.OriginalProfileName!,
        };

        if (!string.IsNullOrEmpty(treatment.OriginalCustomizedName))
            metadata[OriginalCustomizedNameKey] = treatment.OriginalCustomizedName;

        if (treatment.OriginalPercentage is { } percentage)
        {
            metadata[OriginalPercentageKey] = percentage;
            metadata["percentage"] = percentage;
        }

        if (treatment.OriginalTimeshift is { } timeshiftMs)
        {
            metadata[OriginalTimeshiftKey] = timeshiftMs;
            metadata["timeshift"] = Math.Round(timeshiftMs / MillisPerHour, 4);
        }

        if (treatment.OriginalDuration is { } duration)
            metadata[OriginalDurationKey] = duration;

        if (treatment.OriginalEnd is { } end)
            metadata[OriginalEndKey] = end;

        if (!string.IsNullOrEmpty(treatment.ProfileJson))
            metadata["profileJson"] = treatment.ProfileJson;

        return metadata;
    }

    /// <summary>The Note document an effective profile switch span was uploaded as.</summary>
    internal static Treatment Project(IDictionary<string, object>? metadata) =>
        new()
        {
            EventType = metadata.TryReadString(LegacyTreatmentTables.UploadedEventTypeKey),
            ProfileJson = metadata.TryReadString("profileJson"),
            OriginalProfileName = metadata.TryReadString(OriginalProfileNameKey),
            OriginalCustomizedName = metadata.TryReadString(OriginalCustomizedNameKey),
            OriginalPercentage = (int?)metadata.TryReadDecimal(OriginalPercentageKey),
            OriginalTimeshift = (int?)metadata.TryReadDecimal(OriginalTimeshiftKey),
            OriginalDuration = (int?)metadata.TryReadDecimal(OriginalDurationKey),
            OriginalEnd = (long?)metadata.TryReadDecimal(OriginalEndKey),
        };
}

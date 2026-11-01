using System.Globalization;
using System.Text.Json;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.ProfileActivations;

/// <summary>
/// Converts between a <see cref="ProfileActivation"/> and the Profile span metadata
/// <see cref="ProfileSpanMetadata"/> parses: its typed columns, and the residue the legacy treatment
/// it was uploaded as is read back from.
/// </summary>
/// <remarks>
/// The residue (<see cref="IV4Record.AdditionalProperties"/>) is the span metadata less what the typed
/// columns carry, so <see cref="ToSpanMetadata"/> rebuilds the metadata the span would have held.
/// Migration <c>AddProfileActivations</c> applies the same rules in SQL to the spans it converts.
/// </remarks>
internal static class ProfileActivationLegacyFields
{
    internal const string EnteredByKey = "enteredBy";

    /// <summary>
    /// Residue marker for a Profile Switch uploaded without a <c>percentage</c>, which reads back
    /// without one rather than with the 100 its column holds.
    /// </summary>
    internal const string PercentageOmittedKey = "percentageOmitted";

    private const double MillisPerHour = 3_600_000;

    private const string InsulinNameKey = "insulinName";
    private const string InsulinDiaKey = "insulinDia";
    private const string InsulinPeakKey = "insulinPeak";
    private const string InsulinConcentrationKey = "insulinConcentration";
    private const string InsulinCurveKey = "insulinCurve";

    /// <summary>The metadata keys a typed column carries, never kept in the residue.</summary>
    internal static readonly string[] TypedKeys =
    [
        ProfileSpanMetadata.PercentageKey,
        ProfileSpanMetadata.TimeshiftKey,
        ProfileSpanMetadata.OriginalPercentageKey,
        ProfileSpanMetadata.OriginalTimeshiftKey,
        ProfileSpanMetadata.AppKey,
        EnteredByKey,
        StateSpanMetadataExtensions.UtcOffsetKey,
        InsulinNameKey,
        InsulinDiaKey,
        InsulinPeakKey,
        InsulinConcentrationKey,
        InsulinCurveKey,
    ];

    /// <summary>
    /// The activation a Profile span's metadata describes. Its source, origin, and provenance beyond
    /// <c>app</c> and <c>enteredBy</c> are the caller's to set.
    /// </summary>
    /// <param name="end">The end the span carries, which only an old span with no uploaded duration needs.</param>
    /// <param name="superseded">Whether that end is a later switch's start rather than the switch's own length.</param>
    public static ProfileActivation FromSpanMetadata(
        IDictionary<string, object> metadata, DateTime start, DateTime? end = null, bool superseded = false)
    {
        var span = ProfileSpanMetadata.Parse(metadata);
        var baseName = span.BaseProfileName is { Length: > 0 } name ? name : "Default";

        var residue = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in metadata)
        {
            if (!TypedKeys.Contains(key))
                residue[key] = value;
        }

        if (residue.TryGetValue(ProfileSpanMetadata.ProfileNameKey, out var uploadedName)
            && uploadedName is not null && ReadString(uploadedName) == baseName)
            residue.Remove(ProfileSpanMetadata.ProfileNameKey);

        if (span.IsEffective)
            residue.Remove(ProfileSpanMetadata.OriginalProfileNameKey);
        else
        {
            if (span is { UploadedTimeshift: { } uploaded, TimeshiftUnit: { } unit })
            {
                residue[ProfileSpanMetadata.UploadedTimeshiftKey] = uploaded;
                residue[ProfileSpanMetadata.TimeshiftUnitKey] = ProfileSpanMetadata.UnitName(unit);
            }
            if (!metadata.ContainsKey(ProfileSpanMetadata.PercentageKey))
                residue[PercentageOmittedKey] = true;
        }

        var startMills = start.ToMills();
        return new ProfileActivation
        {
            StartTimestamp = start,
            Phase = span.IsEffective ? ProfileActivationPhase.Effective : ProfileActivationPhase.Requested,
            BaseProfileName = baseName,
            Percentage = span.IsEffective
                ? span.OriginalPercentage ?? RoundPercentage(span.Percentage)
                : RoundPercentage(span.Percentage),
            TimeshiftMs = TimeshiftMsOf(span),
            RequestedDurationMs = span.IsEffective
                ? span.OriginalDuration is > 0 ? span.OriginalDuration : null
                : RequestedDurationOf(metadata, span, start, end, superseded),
            EndTimestamp = span.IsEffective ? EffectiveEnd(span, startMills) : null,
            App = span.App,
            EnteredBy = metadata.TryReadString(EnteredByKey),
            UtcOffset = (int?)metadata.TryReadDecimal(StateSpanMetadataExtensions.UtcOffsetKey),
            Snapshot = ProfileActivationSnapshots.From(span.ProfileJson, InsulinOf(metadata)),
            SnapshotIsCustomized = span.IsEffective,
            AdditionalProperties = residue.Count > 0 ? residue : null,
        };
    }

    /// <summary>The span metadata <paramref name="activation"/> was converted from.</summary>
    public static Dictionary<string, object> ToSpanMetadata(ProfileActivation activation)
    {
        var metadata = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (key, value) in activation.AdditionalProperties ?? [])
        {
            if (value is not null and not JsonElement { ValueKind: JsonValueKind.Null })
                metadata[key] = value;
        }

        metadata.TryAdd(ProfileSpanMetadata.ProfileNameKey, activation.BaseProfileName);
        if (!metadata.Remove(PercentageOmittedKey))
            metadata[ProfileSpanMetadata.PercentageKey] = activation.Percentage;

        var hours = activation.TimeshiftMs / MillisPerHour;
        if (metadata.ContainsKey(ProfileSpanMetadata.EventTypeKey))
        {
            metadata.TryAdd(ProfileSpanMetadata.OriginalProfileNameKey, activation.BaseProfileName);
            metadata[ProfileSpanMetadata.OriginalPercentageKey] = activation.Percentage;
            metadata[ProfileSpanMetadata.OriginalTimeshiftKey] = activation.TimeshiftMs;
            metadata[ProfileSpanMetadata.TimeshiftKey] = hours;
        }
        else if (metadata.ContainsKey(ProfileSpanMetadata.TimeshiftUnitKey))
        {
            metadata[ProfileSpanMetadata.TimeshiftKey] = hours;
        }

        if (activation.App is { } app)
            metadata[ProfileSpanMetadata.AppKey] = app;
        if (activation.EnteredBy is { } enteredBy)
            metadata[EnteredByKey] = enteredBy;
        if (activation.UtcOffset is { } utcOffset)
            metadata[StateSpanMetadataExtensions.UtcOffsetKey] = utcOffset;

        return metadata;
    }

    /// <summary>
    /// The end an effective activation was uploaded with, as <see cref="FromSpanMetadata"/> read it:
    /// none unless it asked for a length.
    /// </summary>
    public static DateTime? ReportedEnd(ProfileActivation activation)
    {
        if (activation.Phase != ProfileActivationPhase.Effective || activation.RequestedDurationMs is not > 0)
            return null;

        var originalEnd = activation.AdditionalProperties is { } residue
            && residue.TryGetValue(ProfileSpanMetadata.OriginalEndKey, out var value) && value is not null
                ? (long?)ReadDecimal(value)
                : null;
        return EffectiveEnd(
            new ProfileSpanMetadata { OriginalDuration = activation.RequestedDurationMs, OriginalEnd = originalEnd },
            activation.Mills);
    }

    private static decimal? ReadDecimal(object value) => value switch
    {
        JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetDecimal(out var d) => d,
        JsonElement => null,
        IConvertible convertible => convertible.ToDecimal(CultureInfo.InvariantCulture),
        _ => null,
    };

    private static int RoundPercentage(double? percentage) =>
        percentage is { } value && double.IsFinite(value) ? (int)Math.Round(value) : 100;

    private static long TimeshiftMsOf(ProfileSpanMetadata span)
    {
        if (span.IsEffective && span.OriginalTimeshift is { } originalMs)
            return originalMs;
        if (span is { TimeshiftUnit: ProfileTimeshiftUnit.Milliseconds, UploadedTimeshift: { } uploadedMs })
            return (long)Math.Round(uploadedMs);
        return (long)Math.Round(span.TimeshiftHours * MillisPerHour);
    }

    /// <remarks>
    /// AAPS sends a permanent switch's <c>originalEnd</c> equal to its start, so the end is read only
    /// for a switch that asked for a length.
    /// </remarks>
    private static DateTime? EffectiveEnd(ProfileSpanMetadata span, long startMills)
    {
        if (span.OriginalDuration is not > 0)
            return null;

        var endMills = span.OriginalEnd is { } originalEnd && originalEnd > startMills
            ? originalEnd
            : startMills + span.OriginalDuration.Value;
        return DateTimeOffset.FromUnixTimeMilliseconds(endMills).UtcDateTime;
    }

    /// <remarks>
    /// The uploaded <c>duration</c> in minutes, else <c>durationInMilliseconds</c>. A span written
    /// before either was kept carries its length only as its end, unless a later switch set that end.
    /// </remarks>
    private static long? RequestedDurationOf(
        IDictionary<string, object> metadata, ProfileSpanMetadata span, DateTime start, DateTime? end, bool superseded)
    {
        if (metadata.TryReadDecimal(StateSpanMetadataExtensions.UploadedDurationKey) is { } minutes)
            return minutes > 0 ? (long)Math.Round(minutes * 60_000) : null;
        if (span.DurationInMilliseconds is { } durationMs)
            return durationMs > 0 ? durationMs : null;
        if (end is { } stored && !superseded && stored > start)
            return (long)Math.Round((stored - start).TotalMilliseconds);
        return null;
    }

    private static TreatmentInsulinContext? InsulinOf(IDictionary<string, object> metadata)
    {
        if (metadata.TryReadDecimal(InsulinDiaKey) is not { } dia || dia <= 0)
            return null;
        if (metadata.TryReadDecimal(InsulinPeakKey) is not { } peak || peak <= 0)
            return null;

        return new TreatmentInsulinContext
        {
            PatientInsulinId = Guid.Empty,
            InsulinName = metadata.TryReadString(InsulinNameKey) ?? string.Empty,
            Dia = (double)dia,
            Peak = (int)peak,
            Concentration = (int)(metadata.TryReadDecimal(InsulinConcentrationKey) ?? 100),
            Curve = metadata.TryReadString(InsulinCurveKey) ?? "rapid-acting",
        };
    }

    private static string? ReadString(object value) => value switch
    {
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };
}

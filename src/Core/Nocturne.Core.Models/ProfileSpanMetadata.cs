namespace Nocturne.Core.Models;

/// <summary>The unit a Profile Switch's <c>timeshift</c> was uploaded in.</summary>
public enum ProfileTimeshiftUnit
{
    Hours,
    Milliseconds,
}

/// <summary>
/// The metadata of a <see cref="StateSpanCategory.Profile"/> span, read one way by every consumer: a
/// profile switch's upload as it becomes a <see cref="V4.ProfileActivation"/>, and the activation as
/// it is read back as that upload.
/// </summary>
/// <remarks>
/// <see cref="TimeshiftKey"/> holds the shift in hours. A span written before the uploaded unit was
/// kept (<see cref="TimeshiftUnitKey"/>) holds the value as uploaded instead, so its unit is inferred
/// as at ingest (<see cref="UnitOfUploadedTimeshift"/>). An effective profile switch has no uploaded
/// <c>timeshift</c>: its <see cref="TimeshiftKey"/> has always been hours, converted from
/// <see cref="OriginalTimeshiftKey"/>.
/// </remarks>
public sealed record ProfileSpanMetadata
{
    public const string ProfileNameKey = StateSpanMetadataExtensions.ProfileNameKey;
    public const string PercentageKey = "percentage";
    public const string TimeshiftKey = "timeshift";
    public const string UploadedTimeshiftKey = "uploadedTimeshift";
    public const string TimeshiftUnitKey = "timeshiftUnit";
    public const string ProfileJsonKey = "profileJson";
    public const string DurationInMillisecondsKey = "durationInMilliseconds";

    /// <summary>
    /// The <c>eventType</c> a span was uploaded under, kept only when it is not "Profile Switch": an
    /// AAPS effective profile switch, uploaded as a <c>Note</c>.
    /// </summary>
    public const string EventTypeKey = "eventType";

    public const string OriginalProfileNameKey = "originalProfileName";
    public const string OriginalCustomizedNameKey = "originalCustomizedName";
    public const string OriginalTimeshiftKey = "originalTimeshift";
    public const string OriginalPercentageKey = "originalPercentage";
    public const string OriginalDurationKey = "originalDuration";
    public const string OriginalEndKey = "originalEnd";

    public const string PumpIdKey = "pumpId";
    public const string PumpTypeKey = "pumpType";
    public const string PumpSerialKey = "pumpSerial";
    public const string AppKey = "app";

    private const double MillisPerHour = 3_600_000;

    /// <summary>The name the switch was uploaded under; AAPS names it after its customization (<c>"Rhys (110%)"</c>).</summary>
    public string? ProfileName { get; init; }

    /// <summary>
    /// The stored profile the switch runs: AAPS's <c>originalProfileName</c> when it names one, since
    /// no stored profile is called by the customized name, else <see cref="ProfileName"/>.
    /// </summary>
    public string? BaseProfileName { get; init; }

    public double? Percentage { get; init; }

    public double TimeshiftHours { get; init; }

    /// <summary>The <c>timeshift</c> as uploaded, in <see cref="TimeshiftUnit"/>.</summary>
    public double? UploadedTimeshift { get; init; }

    public ProfileTimeshiftUnit? TimeshiftUnit { get; init; }

    /// <summary>Whether the span is an AAPS effective profile switch rather than a Profile Switch.</summary>
    public bool IsEffective { get; init; }

    public string? EventType { get; init; }

    public string? ProfileJson { get; init; }

    public string? OriginalProfileName { get; init; }

    public string? OriginalCustomizedName { get; init; }

    public long? OriginalTimeshift { get; init; }

    public int? OriginalPercentage { get; init; }

    public long? OriginalDuration { get; init; }

    public long? OriginalEnd { get; init; }

    public long? DurationInMilliseconds { get; init; }

    public long? PumpId { get; init; }

    public string? PumpType { get; init; }

    public string? PumpSerial { get; init; }

    public string? App { get; init; }

    /// <summary>The shift as the profile resolvers apply it: within a day, in milliseconds.</summary>
    public long TimeshiftMs => (long)(TimeshiftHours % 24 * MillisPerHour);

    public static ProfileSpanMetadata Parse(IDictionary<string, object>? metadata)
    {
        var profileName = metadata.TryReadString(ProfileNameKey);
        var originalProfileName = metadata.TryReadString(OriginalProfileNameKey);
        var eventType = metadata.TryReadString(EventTypeKey);
        var isEffective = eventType is not null;
        var timeshift = (double?)metadata.TryReadDecimal(TimeshiftKey);

        var unit = ParseUnit(metadata.TryReadString(TimeshiftUnitKey));
        var uploaded = (double?)metadata.TryReadDecimal(UploadedTimeshiftKey);
        double hours;
        if (unit is not null || isEffective)
        {
            hours = timeshift ?? 0;
        }
        else
        {
            uploaded = timeshift;
            unit = timeshift is { } raw
                ? UnitOfUploadedTimeshift(raw, metadata.TryReadString("enteredBy"), metadata.TryReadString(AppKey), originalProfileName)
                : null;
            hours = timeshift is { } value ? ToHours(value, unit!.Value) : 0;
        }

        return new ProfileSpanMetadata
        {
            ProfileName = profileName,
            BaseProfileName = originalProfileName is { Length: > 0 } ? originalProfileName : profileName,
            Percentage = (double?)metadata.TryReadDecimal(PercentageKey),
            TimeshiftHours = hours,
            UploadedTimeshift = uploaded,
            TimeshiftUnit = unit,
            IsEffective = isEffective,
            EventType = eventType,
            ProfileJson = metadata.TryReadString(ProfileJsonKey),
            OriginalProfileName = originalProfileName,
            OriginalCustomizedName = metadata.TryReadString(OriginalCustomizedNameKey),
            OriginalTimeshift = (long?)metadata.TryReadDecimal(OriginalTimeshiftKey),
            OriginalPercentage = (int?)metadata.TryReadDecimal(OriginalPercentageKey),
            OriginalDuration = (long?)metadata.TryReadDecimal(OriginalDurationKey),
            OriginalEnd = (long?)metadata.TryReadDecimal(OriginalEndKey),
            DurationInMilliseconds = (long?)metadata.TryReadDecimal(DurationInMillisecondsKey),
            PumpId = (long?)metadata.TryReadDecimal(PumpIdKey),
            PumpType = metadata.TryReadString(PumpTypeKey),
            PumpSerial = metadata.TryReadString(PumpSerialKey),
            App = metadata.TryReadString(AppKey),
        };
    }

    /// <summary>
    /// The unit a Profile Switch's <c>timeshift</c> was uploaded in: milliseconds from AAPS
    /// (<c>PS.timeshift</c>, a <c>Long</c> of milliseconds on v1 and v3), hours from everything else.
    /// A value outside a day from an uploader that is not recognised can only be milliseconds.
    /// </summary>
    /// <remarks>
    /// NIGHTSCOUT-COMPAT: Nightscout reads <c>timeshift</c> as hours and applies <c>timeshift % 24</c>
    /// (lib/profilefunctions.js), which zeroes every whole-hour AAPS shift. The shift is stored in hours
    /// and served back as uploaded.
    /// </remarks>
    public static ProfileTimeshiftUnit UnitOfUploadedTimeshift(
        double timeshift, string? enteredBy, string? app, string? originalProfileName)
    {
        var fromAaps = !string.IsNullOrEmpty(originalProfileName)
            || enteredBy?.StartsWith("openaps://AndroidAPS", StringComparison.OrdinalIgnoreCase) == true
            || string.Equals(app, "AAPS", StringComparison.OrdinalIgnoreCase);

        return fromAaps || Math.Abs(timeshift) > 24
            ? ProfileTimeshiftUnit.Milliseconds
            : ProfileTimeshiftUnit.Hours;
    }

    public static double ToHours(double timeshift, ProfileTimeshiftUnit unit) =>
        unit == ProfileTimeshiftUnit.Milliseconds ? Math.Round(timeshift / MillisPerHour, 4) : timeshift;

    public static string UnitName(ProfileTimeshiftUnit unit) =>
        unit == ProfileTimeshiftUnit.Milliseconds ? "ms" : "h";

    private static ProfileTimeshiftUnit? ParseUnit(string? name) => name switch
    {
        "ms" => ProfileTimeshiftUnit.Milliseconds,
        "h" => ProfileTimeshiftUnit.Hours,
        _ => null,
    };
}

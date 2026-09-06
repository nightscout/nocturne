namespace Nocturne.Core.Models.V4;

/// <summary>
/// Patient demographic and profile information. This is a V4-only concept with no legacy equivalent.
/// </summary>
/// <remarks>
/// Stores the patient's diabetes type, diagnosis date, date of birth, and display preferences.
/// <see cref="DiabetesTypeOther"/> is used when <see cref="DiabetesType"/> is
/// <see cref="V4.DiabetesType.Other"/> to capture a freeform description.
/// </remarks>
/// <seealso cref="DiabetesType"/>
/// <seealso cref="PatientDevice"/>
/// <seealso cref="PatientInsulin"/>
/// <seealso cref="TherapySettings"/>
public class PatientRecord
{
    /// <summary>
    /// UUID v7 primary key.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The patient's diabetes classification.
    /// </summary>
    /// <seealso cref="V4.DiabetesType"/>
    public DiabetesType? DiabetesType { get; set; }

    /// <summary>
    /// Freeform description when <see cref="DiabetesType"/> is <see cref="V4.DiabetesType.Other"/>.
    /// </summary>
    public string? DiabetesTypeOther { get; set; }

    /// <summary>
    /// Date the patient was diagnosed with diabetes.
    /// </summary>
    public DateOnly? DiagnosisDate { get; set; }

    /// <summary>
    /// Patient's date of birth.
    /// </summary>
    public DateOnly? DateOfBirth { get; set; }

    /// <summary>
    /// Patient's biological (natal) sex. Optional. Drives sex-specific normative reference ranges
    /// (e.g. sleep-stage composition). Distinct from gender identity / <see cref="Pronouns"/>.
    /// </summary>
    /// <seealso cref="V4.BiologicalSex"/>
    public BiologicalSex? Sex { get; set; }

    /// <summary>
    /// Patient's preferred display name.
    /// </summary>
    public string? PreferredName { get; set; }

    /// <summary>
    /// Patient's preferred pronouns (e.g., "they/them", "she/her").
    /// </summary>
    public string? Pronouns { get; set; }

    /// <summary>
    /// URL to the patient's avatar image.
    /// </summary>
    public string? AvatarUrl { get; set; }

    /// <summary>
    /// IANA timezone id (e.g. <c>Australia/Sydney</c>) the patient lives in. Drives wall-clock
    /// interpretation across alerts (time-of-day windows, DND schedules), analytics, and the
    /// glucose-bucket schedule resolver. Source of truth — supersedes the per-profile
    /// <see cref="TherapySettings.Timezone"/> which was structurally the wrong home (a single
    /// human can't be in two timezones simultaneously even if they have multiple profiles).
    /// </summary>
    public string? Timezone { get; set; }

    /// <summary>
    /// When this record was first created (UTC).
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When this record was last modified (UTC).
    /// </summary>
    public DateTime ModifiedAt { get; set; }
}

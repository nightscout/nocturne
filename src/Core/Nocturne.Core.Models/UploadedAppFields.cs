namespace Nocturne.Core.Models;

/// <summary>
/// The <c>app</c> an uploader names itself by, the <c>isReadOnly</c> it marks a treatment with, and
/// the <c>enteredBy</c> and <c>device</c> it sends, which AAPS reads back to know its own records.
/// A V4 record keeps <c>app</c> in its own <see cref="V4.V4RecordBase.App"/> and the rest in its
/// additional properties. A state span keeps all four in its metadata (<see cref="KeepInMetadata"/>). A
/// profile activation keeps <c>app</c> and <c>enteredBy</c> in its columns and <c>device</c> and
/// <c>isReadOnly</c> in the residue that <c>ProfileActivationLegacyFields</c> converts to and from that metadata.
/// </summary>
public static class UploadedAppFields
{
    public const string IsReadOnlyKey = "isReadOnly";

    /// <summary>The key a record keeps the <c>enteredBy</c> its uploader sent under.</summary>
    public const string EnteredByKey = "enteredBy";

    /// <summary>The key a record keeps the <c>device</c> its uploader sent under.</summary>
    public const string DeviceKey = "device";

    /// <summary>The key a treatment names its uploading app under.</summary>
    public const string AppKey = "app";

    /// <summary>The <c>app</c> the treatment was uploaded with, when it is a string.</summary>
    public static string? AppOf(Treatment treatment) =>
        KeptField.String(KeptField.Nullable(treatment.AdditionalProperties), AppKey);

    /// <summary>The <c>device</c> the treatment was uploaded with, when it is a string.</summary>
    public static string? DeviceOf(Treatment treatment) =>
        KeptField.String(KeptField.Nullable(treatment.AdditionalProperties), DeviceKey);

    public static Dictionary<string, object?>? Keep(Dictionary<string, object?>? record, Treatment treatment) =>
        KeptField.Add(record, IsReadOnlyKey, treatment.IsReadOnly);

    public static void Restore(Treatment treatment, IReadOnlyDictionary<string, object?>? record, string? app)
    {
        treatment.IsReadOnly = KeptField.Bool(record, IsReadOnlyKey);
        if (app is not null)
            (treatment.AdditionalProperties ??= new())[AppKey] = app;
    }

    /// <summary>
    /// Keeps the <c>enteredBy</c> and <c>device</c> the treatment was uploaded with, each only when
    /// sent, since the record's <c>Device</c> holds one or the other and cannot say which.
    /// </summary>
    public static Dictionary<string, object?>? KeepUploader(Dictionary<string, object?>? record, Treatment treatment)
    {
        record = KeptField.Add(record, EnteredByKey, treatment.EnteredBy);
        return KeptField.Add(record, DeviceKey, DeviceOf(treatment));
    }

    /// <summary>
    /// Serves the uploaded <c>enteredBy</c> and <c>device</c> exactly. A record that kept neither (one
    /// stored before they were kept, or written by a V4 client) serves
    /// <paramref name="unkeptEnteredBy"/> and <paramref name="unkeptDevice"/> instead.
    /// </summary>
    public static void RestoreUploader(
        Treatment treatment,
        IReadOnlyDictionary<string, object?>? record,
        string? unkeptEnteredBy,
        string? unkeptDevice = null)
    {
        var enteredBy = KeptField.String(record, EnteredByKey);
        var device = KeptField.String(record, DeviceKey);
        if (enteredBy is null && device is null)
        {
            enteredBy = unkeptEnteredBy;
            device = string.IsNullOrEmpty(unkeptDevice) ? null : unkeptDevice;
        }

        treatment.EnteredBy = enteredBy;
        if (device is not null)
            (treatment.AdditionalProperties ??= new())[DeviceKey] = device;
    }

    /// <summary>Keeps <c>isReadOnly</c>, <c>enteredBy</c> and <c>device</c> in one record's additional properties.</summary>
    public static Dictionary<string, object?>? KeepAll(Dictionary<string, object?>? record, Treatment treatment) =>
        KeepUploader(Keep(record, treatment), treatment);

    /// <summary>Serves what <see cref="KeepAll"/> kept, and the record's <paramref name="app"/>.</summary>
    public static void RestoreAll(
        Treatment treatment,
        IReadOnlyDictionary<string, object?>? record,
        string? app,
        string? unkeptEnteredBy,
        string? unkeptDevice = null)
    {
        Restore(treatment, record, app);
        RestoreUploader(treatment, record, unkeptEnteredBy, unkeptDevice);
    }

    /// <summary>
    /// Keeps what <see cref="KeepAll"/> keeps, and the <c>app</c> when sent, in the metadata of the span or
    /// profile activation the treatment is stored as.
    /// </summary>
    public static void KeepInMetadata(IDictionary<string, object> metadata, Treatment treatment) =>
        KeptField.CopyInto(metadata, KeptField.Add(KeepAll(null, treatment), AppKey, AppOf(treatment)));

    /// <summary>Serves what <see cref="KeepInMetadata"/> kept. A span stores no V4 client's row, so nothing is unkept.</summary>
    public static void RestoreFromMetadata(Treatment treatment, IDictionary<string, object>? metadata)
    {
        var record = KeptField.OfMetadata(metadata);
        RestoreAll(treatment, record, KeptField.String(record, AppKey), unkeptEnteredBy: null);
    }
}

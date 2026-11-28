namespace Nocturne.Core.Models;

/// <summary>
/// The <c>app</c> an uploader names itself by, the <c>isReadOnly</c> it marks a treatment with, and
/// the <c>enteredBy</c> and <c>device</c> it sends, which AAPS reads back to know its own records.
/// <c>app</c> is the V4 record's own <see cref="V4.V4RecordBase.App"/>; <c>isReadOnly</c>,
/// <c>enteredBy</c> and <c>device</c> are kept in its additional properties.
/// </summary>
public static class UploadedAppFields
{
    public const string IsReadOnlyKey = "isReadOnly";

    /// <summary>The key a record keeps the <c>enteredBy</c> its uploader sent under.</summary>
    public const string EnteredByKey = "enteredBy";

    /// <summary>The key a record keeps the <c>device</c> its uploader sent under.</summary>
    public const string DeviceKey = "device";

    private const string App = "app";

    public static Dictionary<string, object?>? Keep(Dictionary<string, object?>? record, Treatment treatment) =>
        KeptField.Add(record, IsReadOnlyKey, treatment.IsReadOnly);

    public static void Restore(Treatment treatment, IReadOnlyDictionary<string, object?>? record, string? app)
    {
        treatment.IsReadOnly = KeptField.Bool(record, IsReadOnlyKey);
        if (app is not null)
            (treatment.AdditionalProperties ??= new())[App] = app;
    }

    /// <summary>
    /// Keeps the <c>enteredBy</c> and <c>device</c> the treatment was uploaded with, each only when
    /// sent, since the record's <c>Device</c> holds one or the other and cannot say which.
    /// </summary>
    public static Dictionary<string, object?>? KeepUploader(Dictionary<string, object?>? record, Treatment treatment)
    {
        record = KeptField.Add(record, EnteredByKey, treatment.EnteredBy);
        return KeptField.Add(record, DeviceKey, KeptField.String(treatment.AdditionalProperties, DeviceKey));
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
}

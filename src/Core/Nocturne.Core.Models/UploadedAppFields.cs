namespace Nocturne.Core.Models;

/// <summary>
/// The <c>app</c> an uploader names itself by and the <c>isReadOnly</c> it marks a treatment with,
/// which AAPS reads back to know its own records. <c>app</c> is the V4 record's own
/// <see cref="V4.V4RecordBase.App"/>; <c>isReadOnly</c> is kept in its additional properties.
/// </summary>
public static class UploadedAppFields
{
    public const string IsReadOnlyKey = "isReadOnly";

    /// <summary>The key a record that cannot serve its <c>enteredBy</c> from its device keeps it under.</summary>
    public const string EnteredByKey = "enteredBy";

    private const string App = "app";

    public static Dictionary<string, object?>? Keep(Dictionary<string, object?>? record, Treatment treatment) =>
        KeptField.Add(record, IsReadOnlyKey, treatment.IsReadOnly);

    public static void Restore(Treatment treatment, IReadOnlyDictionary<string, object?>? record, string? app)
    {
        treatment.IsReadOnly = KeptField.Bool(record, IsReadOnlyKey);
        if (app is not null)
            (treatment.AdditionalProperties ??= new())[App] = app;
    }
}

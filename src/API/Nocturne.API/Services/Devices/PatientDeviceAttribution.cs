using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Devices;

/// <summary>
/// Applies the <c>patientDeviceId</c> field of a V4 upsert request to the record being written,
/// and stamps whatever the caller left to the server.
/// </summary>
/// <seealso cref="IPatientDeviceStamper"/>
internal static class PatientDeviceAttribution
{
    /// <summary>
    /// The <c>patientDeviceId</c> value meaning "this record belongs to no registered device".
    /// Omitting the field cannot express that — omission means "leave attribution to the server" —
    /// and neither the generated SDK clients nor the form bindings preserve absent-versus-null,
    /// so the clear travels as an in-band value rather than as a JSON null.
    /// </summary>
    public static readonly Guid Clear = Guid.Empty;

    /// <summary>
    /// Applies <paramref name="requested"/> to <paramref name="model"/>: an explicit id is validated
    /// against the caller's registered devices, <see cref="Clear"/> unattributes the record, and an
    /// absent value falls back to <paramref name="existing"/> (null on create).
    /// </summary>
    /// <returns>A problem detail when the requested id doesn't resolve, otherwise <c>null</c>.</returns>
    public static Task<string?> ApplyAsync(
        IDeviceAttributed model,
        Guid? requested,
        Guid? existing,
        IPatientDeviceRepository devices,
        IPatientDeviceStamper stamper,
        IReadOnlyList<DeviceCategory> categories,
        CancellationToken ct)
    {
        model.PatientDeviceId = existing;
        return ApplyManyAsync([(model, requested)], devices, stamper, categories, model.DataSource, ct);
    }

    /// <summary>
    /// Batch form of <see cref="ApplyAsync"/> for bulk creates: cleared records are excluded from the
    /// single stamper pass so the server cannot immediately re-attribute what the caller unattributed.
    /// </summary>
    /// <returns>A problem detail for the first requested id that doesn't resolve, otherwise <c>null</c>.</returns>
    public static async Task<string?> ApplyManyAsync(
        IReadOnlyList<(IDeviceAttributed Model, Guid? Requested)> items,
        IPatientDeviceRepository devices,
        IPatientDeviceStamper stamper,
        IReadOnlyList<DeviceCategory> categories,
        string? batchSource,
        CancellationToken ct)
    {
        var stampable = new List<IDeviceAttributed>(items.Count);

        foreach (var (model, requested) in items)
        {
            if (requested == Clear)
            {
                model.PatientDeviceId = null;
                continue;
            }

            if (requested is { } id)
            {
                if (await devices.GetByIdAsync(id, ct) is null)
                    return $"patientDeviceId '{id}' does not resolve to a registered patient device";

                model.PatientDeviceId = id;
            }

            stampable.Add(model);
        }

        if (stampable.Count > 0)
            await stamper.StampAsync(stampable, categories, batchSource, ct);

        return null;
    }
}

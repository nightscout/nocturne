using System.Text.Json;
using Nocturne.Connectors.Core.Constants;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Entities;

namespace Nocturne.API.Services.Devices;

/// <summary>
/// Gives a newly registered device the trackers its hardware wears out on: a sensor tracker for a CGM
/// (and a transmitter tracker when the transmitter is separate), a pod tracker for a patch pump, and
/// infusion-site and reservoir trackers for a tubed pump. Lifespans come from the device's
/// <see cref="DeviceCatalogEntry"/>, so a device without a catalog id gets none.
/// </summary>
/// <remarks>
/// Definitions are per user and named after the device model, found-or-created by name, so
/// registering the same model twice reuses its trackers while switching models adds the new model's.
/// </remarks>
public interface IDeviceTrackerProvisioner
{
    /// <summary>
    /// Creates the trackers <paramref name="device"/>'s catalog entry calls for that
    /// <paramref name="userId"/> does not already have. Returns the number created.
    /// </summary>
    Task<int> ProvisionAsync(PatientDevice device, string userId, CancellationToken ct = default);
}

/// <inheritdoc />
internal sealed class DeviceTrackerProvisioner(ITrackerRepository trackers) : IDeviceTrackerProvisioner
{
    /// <summary>A tracker definition a catalog entry calls for.</summary>
    internal sealed record TrackerSpec(
        string Name,
        TrackerCategory Category,
        string Icon,
        int LifespanHours,
        IReadOnlyList<string> Triggers);

    /// <inheritdoc />
    public async Task<int> ProvisionAsync(PatientDevice device, string userId, CancellationToken ct = default)
    {
        if (device.CatalogId is not { } catalogId || DeviceCatalog.GetById(catalogId) is not { } entry)
            return 0;

        var specs = SpecsFor(entry);
        if (specs.Count == 0)
            return 0;

        var existing = await trackers.GetDefinitionsForUserAsync(userId, ct);
        var created = 0;
        foreach (var spec in specs)
        {
            if (existing.Any(d => string.Equals(d.Name, spec.Name, StringComparison.OrdinalIgnoreCase)))
                continue;

            await trackers.CreateDefinitionAsync(new TrackerDefinitionEntity
            {
                UserId = userId,
                Name = spec.Name,
                Category = spec.Category,
                Icon = spec.Icon,
                LifespanHours = spec.LifespanHours,
                TriggerEventTypes = JsonSerializer.Serialize(spec.Triggers),
                StartEventType = spec.Triggers.FirstOrDefault(),
                Mode = TrackerMode.Duration,
            }, ct);
            created++;
        }

        return created;
    }

    /// <summary>The trackers <paramref name="entry"/> calls for, empty when it carries no wear data.</summary>
    internal static IReadOnlyList<TrackerSpec> SpecsFor(DeviceCatalogEntry entry)
    {
        var specs = new List<TrackerSpec>();

        if (entry.Cgm is { } cgm)
        {
            specs.Add(new($"{entry.Name} sensor", TrackerCategory.Sensor, "radio",
                cgm.SensorDurationDays * 24,
                [TreatmentTypes.SensorStart, TreatmentTypes.SensorChange]));

            if (cgm is { HasSeparateTransmitter: true, TransmitterDurationDays: { } transmitterDays })
                specs.Add(new($"{entry.Name} transmitter", TrackerCategory.Battery, "battery",
                    transmitterDays * 24, []));
        }

        if (entry.Pump is { } pump)
        {
            if (pump.IsPatch)
            {
                specs.Add(new($"{entry.Name} pod", TrackerCategory.Cannula, "syringe",
                    pump.SiteDurationHours,
                    [TreatmentTypes.PodChange, TreatmentTypes.SiteChange]));
            }
            else
            {
                specs.Add(new($"{entry.Name} infusion site", TrackerCategory.Cannula, "syringe",
                    pump.SiteDurationHours,
                    [TreatmentTypes.SiteChange, TreatmentTypes.CannulaChange]));

                if (pump.ReservoirDurationHours is { } reservoirHours)
                    specs.Add(new($"{entry.Name} reservoir", TrackerCategory.Reservoir, "cylinder",
                        reservoirHours,
                        [TreatmentTypes.InsulinChange, TreatmentTypes.ReservoirChangeEvent]));
            }
        }

        return specs;
    }
}

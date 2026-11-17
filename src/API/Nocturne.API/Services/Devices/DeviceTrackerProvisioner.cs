using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Nocturne.API.Services.Monitoring;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Abstractions;

namespace Nocturne.API.Services.Devices;

/// <summary>
/// Gives a newly registered device the trackers its hardware wears out on: a sensor tracker for a CGM
/// (and a transmitter tracker when the transmitter is separate), and for a pump one tracker per
/// <see cref="PumpComponent"/> that is <see cref="PumpComponent.TrackedByDefault"/> and has a
/// lifespan, such as a pod, or an infusion site and a reservoir. Lifespans come from the device's
/// <see cref="DeviceCatalogEntry"/>, so a device without a catalog id gets none.
/// </summary>
/// <remarks>
/// Definitions are the tenant's and named after the device model. A spec is skipped when the tenant
/// has a tracker of its name, so registering the same model twice reuses its trackers, or one that
/// already does its job (<see cref="LegacyFallbackRules.Follows"/>), which a second tracker would
/// duplicate: switching CGM models keeps the sensor tracker as it is, while switching between a
/// pump with a pod and one with an infusion set adds the new type's site tracker. The trackers have
/// no notification thresholds; a member adds them. Each starts at the latest event that restarts it
/// (<see cref="ConsumableTrackerWriter"/>), under the consumable lock the legacy conversion takes.
/// </remarks>
public interface IDeviceTrackerProvisioner
{
    /// <summary>
    /// Creates the trackers <paramref name="device"/>'s catalog entry calls for that the tenant does
    /// not already have, recording <paramref name="userId"/> as their creator. Returns the number
    /// created, 0 when the store fails: the device is saved already, and the legacy fallbacks still
    /// offer the consumables.
    /// </summary>
    Task<int> ProvisionAsync(PatientDevice device, string userId, CancellationToken ct = default);
}

/// <inheritdoc />
internal sealed class DeviceTrackerProvisioner(
    ITrackerRepository trackers,
    ConsumableTrackerWriter writer,
    ILogger<DeviceTrackerProvisioner> logger)
    : IDeviceTrackerProvisioner
{
    /// <inheritdoc />
    public async Task<int> ProvisionAsync(PatientDevice device, string userId, CancellationToken ct = default)
    {
        if (device.CatalogId is not { } catalogId || DeviceCatalog.GetById(catalogId) is not { } entry)
            return 0;

        var specs = ConsumableTrackerSpecs.SpecsFor(entry);
        if (specs.Count == 0)
            return 0;

        List<CreatedConsumableTracker> created;
        try
        {
            created = await trackers.ExecuteUnderConsumableLockAsync(
                async (tx, unit, token) =>
                {
                    var existing = await tx.GetAllDefinitionsAsync(token);
                    var made = new List<CreatedConsumableTracker>();
                    foreach (var spec in specs)
                    {
                        if (existing.Any(d => string.Equals(d.Name, spec.Name, StringComparison.OrdinalIgnoreCase)))
                            continue;
                        if (existing.Any(d => LegacyFallbackRules.Follows(d, spec)))
                            continue;

                        made.Add(await writer.CreateAsync(tx, unit, spec, userId, withAlerts: false, token));
                    }
                    return made;
                },
                async (tx, attempt, token) =>
                {
                    foreach (var tracker in attempt)
                        if (!await ConsumableTrackerWriter.LandedAsync(tx, tracker, token))
                            return false;
                    return true;
                },
                ct);
        }
        catch (DbUpdateException ex)
        {
            logger.LogError(ex, "Creating the trackers of device {DeviceId} failed", device.Id);
            return 0;
        }
        catch (DbException ex)
        {
            logger.LogError(ex, "Creating the trackers of device {DeviceId} failed", device.Id);
            return 0;
        }
        catch (RetryLimitExceededException ex)
        {
            logger.LogError(ex, "Creating the trackers of device {DeviceId} failed", device.Id);
            return 0;
        }

        foreach (var tracker in created)
        {
            try
            {
                await writer.PublishAsync(trackers, tracker, CancellationToken.None);
            }
            catch (DbUpdateException ex)
            {
                logger.LogError(ex, "Announcing tracker {DefinitionId} failed", tracker.Definition.Id);
            }
            catch (DbException ex)
            {
                logger.LogError(ex, "Announcing tracker {DefinitionId} failed", tracker.Definition.Id);
            }
            catch (RetryLimitExceededException ex)
            {
                logger.LogError(ex, "Announcing tracker {DefinitionId} failed", tracker.Definition.Id);
            }
        }

        return created.Count;
    }
}

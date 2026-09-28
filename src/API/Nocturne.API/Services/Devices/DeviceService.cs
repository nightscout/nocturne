using System.Collections.Concurrent;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.API.Services.Devices;

/// <summary>
/// Resolves or creates canonical <see cref="Device"/> records by category, type, and serial number.
/// Results are cached in a per-tenant in-memory <see cref="ConcurrentDictionary{TKey,TValue}"/> to
/// avoid redundant database lookups during bulk decomposition.
/// </summary>
/// <seealso cref="IDeviceService"/>
public class DeviceService : IDeviceService
{
    private readonly IDeviceRepository _repository;
    private readonly IPatientDeviceRepository _patientDeviceRepository;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly ConcurrentDictionary<(string, string, string, string), Device> _cache = new();
    private readonly ConcurrentDictionary<(string, Guid), IReadOnlyList<PatientDevice>> _patientDeviceCache = new();

    private string TenantCacheId => _tenantAccessor.Context?.TenantId.ToString()
        ?? throw new InvalidOperationException("Tenant context is not resolved");

    public DeviceService(IDeviceRepository repository, IPatientDeviceRepository patientDeviceRepository, ITenantAccessor tenantAccessor)
    {
        _repository = repository;
        _patientDeviceRepository = patientDeviceRepository;
        _tenantAccessor = tenantAccessor;
    }

    public async Task<Guid?> ResolveAsync(DeviceCategory category, string? type, string? serial, long mills, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(serial))
            return null;

        var tenantId = TenantCacheId;
        var key = (tenantId, category.ToString(), type, serial);
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(mills).UtcDateTime;
        if (_cache.TryGetValue(key, out var cached))
        {
            await AdvanceLastSeenAsync(cached, timestamp, ct);
            return cached.Id;
        }

        var existing = await _repository.FindByCategoryTypeAndSerialAsync(category, type, serial, ct);
        if (existing is not null)
        {
            await AdvanceLastSeenAsync(existing, timestamp, ct);
            _cache[key] = existing;
            return existing.Id;
        }

        var device = new Device
        {
            Id = Guid.CreateVersion7(),
            Category = category,
            Type = type,
            Serial = serial,
            FirstSeenTimestamp = timestamp,
            LastSeenTimestamp = timestamp
        };
        var created = await _repository.CreateAsync(device, WriteOrigin.Live, ct);
        _cache[key] = created;
        return created.Id;
    }

    private async Task AdvanceLastSeenAsync(Device device, DateTime timestamp, CancellationToken ct)
    {
        if (timestamp <= device.LastSeenTimestamp)
            return;

        var persisted = device.LastSeenTimestamp;
        device.LastSeenTimestamp = timestamp;
        try
        {
            await _repository.UpdateAsync(device.Id, device, WriteOrigin.Live, ct);
        }
        catch
        {
            // The cached device outlives a failed page in a migration's scope, so it must not
            // claim a last seen the database never took.
            device.LastSeenTimestamp = persisted;
            throw;
        }
    }

    public async Task<Guid?> ResolvePatientDeviceAsync(Guid? deviceId, long mills, CancellationToken ct = default)
    {
        if (deviceId is null)
            return null;

        var tenantId = TenantCacheId;
        var key = (tenantId, deviceId.Value);

        if (!_patientDeviceCache.TryGetValue(key, out var patientDevices))
        {
            patientDevices = await _patientDeviceRepository.GetByDeviceIdAsync(deviceId.Value, ct);
            _patientDeviceCache[key] = patientDevices;
        }

        var date = DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(mills).UtcDateTime);

        return patientDevices.FirstOrDefault(pd =>
            (pd.StartDate is null || pd.StartDate <= date) &&
            (pd.EndDate is null || pd.EndDate >= date))?.Id;
    }
}

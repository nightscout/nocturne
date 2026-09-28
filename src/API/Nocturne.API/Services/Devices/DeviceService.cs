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
    private readonly Dictionary<Guid, (Device Device, DateTime FirstPersisted, DateTime LastPersisted)> _deferredSeen = [];
    private int _deferDepth;

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
            await WidenSeenWindowAsync(cached, timestamp, ct);
            return cached.Id;
        }

        var existing = await _repository.FindByCategoryTypeAndSerialAsync(category, type, serial, ct);
        if (existing is not null)
        {
            await WidenSeenWindowAsync(existing, timestamp, ct);
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

    private async Task WidenSeenWindowAsync(Device device, DateTime timestamp, CancellationToken ct)
    {
        // The stored window only ever widens, so an instant inside this copy's window is inside it too.
        if (timestamp >= device.FirstSeenTimestamp && timestamp <= device.LastSeenTimestamp)
            return;

        if (_deferDepth > 0)
        {
            _deferredSeen.TryAdd(device.Id, (device, device.FirstSeenTimestamp, device.LastSeenTimestamp));
            Widen(device, timestamp);
            return;
        }

        await _repository.WidenSeenWindowAsync(device.Id, timestamp, WriteOrigin.Live, ct);

        // Widened only once the database took it: the cached device outlives a failed page in a
        // migration's scope and must not claim a window the database never stored.
        Widen(device, timestamp);
    }

    private static void Widen(Device device, DateTime timestamp)
    {
        if (timestamp > device.LastSeenTimestamp)
            device.LastSeenTimestamp = timestamp;
        if (timestamp < device.FirstSeenTimestamp)
            device.FirstSeenTimestamp = timestamp;
    }

    public IAsyncDisposable DeferLastSeen(CancellationToken ct = default)
    {
        _deferDepth++;
        return new SeenWindowDeferral(this, ct);
    }

    /// <summary>
    /// A deferred widening is already on the cached device, so a failed flush rolls back every device
    /// it has not yet written, for the reason <see cref="WidenSeenWindowAsync(Device, DateTime, CancellationToken)"/>
    /// widens its copy only after the write.
    /// </summary>
    private async Task FlushDeferredSeenAsync(CancellationToken ct)
    {
        if (--_deferDepth > 0)
            return;

        var pending = _deferredSeen.Values.ToList();
        _deferredSeen.Clear();
        for (var i = 0; i < pending.Count; i++)
        {
            var (device, firstPersisted, lastPersisted) = pending[i];
            try
            {
                if (device.LastSeenTimestamp > lastPersisted)
                    await _repository.WidenSeenWindowAsync(device.Id, device.LastSeenTimestamp, WriteOrigin.Live, ct);
                if (device.FirstSeenTimestamp < firstPersisted)
                    await _repository.WidenSeenWindowAsync(device.Id, device.FirstSeenTimestamp, WriteOrigin.Live, ct);
            }
            catch
            {
                foreach (var (d, first, last) in pending.Skip(i))
                {
                    d.FirstSeenTimestamp = first;
                    d.LastSeenTimestamp = last;
                }
                throw;
            }
        }
    }

    private sealed class SeenWindowDeferral(DeviceService owner, CancellationToken ct) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => new(owner.FlushDeferredSeenAsync(ct));
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

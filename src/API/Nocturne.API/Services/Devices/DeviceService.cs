using System.Collections.Concurrent;
using Nocturne.Core.Clinical.Devices;
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
    private readonly DeviceDateClockResolver _deviceDates;
    private (string TenantId, DeviceDateClock Clock)? _deviceDateClock;
    private readonly ConcurrentDictionary<(string, string, string, string), Device> _cache = new();
    private readonly ConcurrentDictionary<(string, Guid), IReadOnlyList<PatientDevice>> _patientDeviceCache = new();
    private readonly Dictionary<Guid, (Device Device, DateTime FirstPersisted, DateTime LastPersisted)> _deferredSeen = [];
    private int _deferDepth;

    private string TenantCacheId => _tenantAccessor.Context?.TenantId.ToString()
        ?? throw new InvalidOperationException("Tenant context is not resolved");

    public DeviceService(IDeviceRepository repository, IPatientDeviceRepository patientDeviceRepository,
        ITenantAccessor tenantAccessor,
        DeviceDateClockResolver deviceDates)
    {
        _repository = repository;
        _patientDeviceRepository = patientDeviceRepository;
        _tenantAccessor = tenantAccessor;
        _deviceDates = deviceDates;
    }

    public async Task<Guid?> ResolveAsync(DeviceCategory category, string? type, string? serial, long mills, CancellationToken ct = default)
        => (await ResolveManyAsync([new DeviceSighting(category, type, serial, mills)], ct))[0];

    /// <remarks>
    /// A device created here starts its window at its first sighting in <paramref name="sightings"/>,
    /// as a sequence of <see cref="ResolveAsync"/> calls would create it, and is then widened to the rest.
    /// </remarks>
    public async Task<IReadOnlyList<Guid?>> ResolveManyAsync(IReadOnlyList<DeviceSighting> sightings, CancellationToken ct = default)
    {
        var tenantId = TenantCacheId;
        var keys = sightings
            .Select(s => string.IsNullOrEmpty(s.Type) || string.IsNullOrEmpty(s.Serial)
                ? ((string, string, string, string)?)null
                : (tenantId, s.Category.ToString(), s.Type, s.Serial))
            .ToList();

        var unresolved = keys.OfType<(string, string, string, string)>()
            .Distinct()
            .Where(key => !_cache.ContainsKey(key))
            .ToList();
        if (unresolved.Count > 0)
        {
            var stored = await _repository.FindByIdentitiesAsync(
                [.. unresolved.Select(k => (Enum.Parse<DeviceCategory>(k.Item2), k.Item3, k.Item4))], ct);
            foreach (var device in stored)
                _cache.TryAdd((tenantId, device.Category.ToString(), device.Type, device.Serial), device);
        }

        var window = new Dictionary<(string, string, string, string), (DateTime First, DateTime Last)>();
        for (var i = 0; i < sightings.Count; i++)
        {
            if (keys[i] is not { } key)
                continue;

            var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(sightings[i].Mills).UtcDateTime;
            if (!_cache.ContainsKey(key))
            {
                _cache[key] = await _repository.CreateAsync(new Device
                {
                    Id = Guid.CreateVersion7(),
                    Category = sightings[i].Category,
                    Type = key.Item3,
                    Serial = key.Item4,
                    FirstSeenTimestamp = timestamp,
                    LastSeenTimestamp = timestamp
                }, WriteOrigin.Live, ct);
            }

            window[key] = window.TryGetValue(key, out var seen)
                ? (timestamp < seen.First ? timestamp : seen.First, timestamp > seen.Last ? timestamp : seen.Last)
                : (timestamp, timestamp);
        }

        foreach (var (key, (first, last)) in window)
        {
            await WidenSeenWindowAsync(_cache[key], first, ct);
            await WidenSeenWindowAsync(_cache[key], last, ct);
        }

        return [.. keys.Select(key => key is { } k ? _cache[k].Id : (Guid?)null)];
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

    public ILastSeenDeferral DeferLastSeen(CancellationToken ct = default)
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

        var pending = TakeDeferredSeen();
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
                Restore(pending.Skip(i));
                throw;
            }
        }
    }

    private void DiscardDeferredSeen()
    {
        if (--_deferDepth > 0)
            return;

        Restore(TakeDeferredSeen());
    }

    private List<(Device Device, DateTime FirstPersisted, DateTime LastPersisted)> TakeDeferredSeen()
    {
        var pending = _deferredSeen.Values.ToList();
        _deferredSeen.Clear();
        return pending;
    }

    private static void Restore(IEnumerable<(Device Device, DateTime FirstPersisted, DateTime LastPersisted)> pending)
    {
        foreach (var (device, first, last) in pending)
        {
            device.FirstSeenTimestamp = first;
            device.LastSeenTimestamp = last;
        }
    }

    private sealed class SeenWindowDeferral(DeviceService owner, CancellationToken ct) : ILastSeenDeferral
    {
        private bool _settled;

        public ValueTask DisposeAsync()
        {
            if (_settled)
                return ValueTask.CompletedTask;

            _settled = true;
            return new(owner.FlushDeferredSeenAsync(ct));
        }

        public void Discard()
        {
            if (_settled)
                return;

            _settled = true;
            owner.DiscardDeferredSeen();
        }
    }

    public async Task<Guid?> ResolvePatientDeviceAsync(Guid? deviceId, long mills, CancellationToken ct = default)
        => (await GetPatientDeviceLinksAsync([deviceId], ct)).Resolve(deviceId, mills);

    public async Task<IPatientDeviceLinks> GetPatientDeviceLinksAsync(IEnumerable<Guid?> deviceIds, CancellationToken ct = default)
    {
        var tenantId = TenantCacheId;
        var ids = deviceIds.OfType<Guid>().Distinct().ToList();

        var unread = ids.Where(id => !_patientDeviceCache.ContainsKey((tenantId, id))).ToList();
        if (unread.Count > 0)
        {
            var byDevice = (await _patientDeviceRepository.GetByDeviceIdsAsync(unread, ct)).ToLookup(pd => pd.DeviceId);
            foreach (var id in unread)
                _patientDeviceCache[(tenantId, id)] = [.. byDevice[id]];
        }

        var links = ids.ToDictionary(id => id, id => _patientDeviceCache[(tenantId, id)]);
        var clock = links.Values.Any(l => l.Any(pd => pd.StartDate is not null || pd.EndDate is not null))
            ? await DeviceDateClockAsync(tenantId, ct)
            : null;
        return new PatientDeviceLinks(links, clock);
    }

    private sealed class PatientDeviceLinks(
        IReadOnlyDictionary<Guid, IReadOnlyList<PatientDevice>> links, DeviceDateClock? clock) : IPatientDeviceLinks
    {
        public Guid? Resolve(Guid? deviceId, long mills)
        {
            if (deviceId is not { } id || !links.TryGetValue(id, out var patientDevices))
                return null;

            if (clock is null || !patientDevices.Any(pd => pd.StartDate is not null || pd.EndDate is not null))
                return patientDevices.FirstOrDefault()?.Id;

            var date = PatientDeviceUsage.LocalDate(
                DateTimeOffset.FromUnixTimeMilliseconds(mills).UtcDateTime, utcOffsetMinutes: null, clock);
            return patientDevices.FirstOrDefault(pd => PatientDeviceUsage.Covers(pd, date))?.Id;
        }
    }

    private async Task<DeviceDateClock> DeviceDateClockAsync(string tenantId, CancellationToken ct)
    {
        if (_deviceDateClock is { } cached && cached.TenantId == tenantId)
            return cached.Clock;

        var clock = await _deviceDates.ResolveAsync(ct);
        _deviceDateClock = (tenantId, clock);
        return clock;
    }
}

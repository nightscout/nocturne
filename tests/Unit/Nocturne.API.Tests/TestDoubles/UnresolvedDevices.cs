using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.TestDoubles;

/// <summary>A device registry that knows no device and links nothing, for a test that does not look at devices.</summary>
internal sealed class UnresolvedDevices : IDeviceService
{
    public Task<Guid?> ResolveAsync(DeviceCategory category, string? type, string? serial, long mills, CancellationToken ct = default) =>
        Task.FromResult<Guid?>(null);

    public Task<IReadOnlyList<Guid?>> ResolveManyAsync(IReadOnlyList<DeviceSighting> sightings, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Guid?>>([.. sightings.Select(_ => (Guid?)null)]);

    public Task<Guid?> ResolvePatientDeviceAsync(Guid? deviceId, long mills, CancellationToken ct = default) =>
        Task.FromResult<Guid?>(null);

    public Task<IPatientDeviceLinks> GetPatientDeviceLinksAsync(IEnumerable<Guid?> deviceIds, CancellationToken ct = default) =>
        Task.FromResult<IPatientDeviceLinks>(new NoLinks());

    public ILastSeenDeferral DeferLastSeen(CancellationToken ct = default) => new NoDeferral();

    private sealed class NoLinks : IPatientDeviceLinks
    {
        public Guid? Resolve(Guid? deviceId, long mills) => null;
    }

    private sealed class NoDeferral : ILastSeenDeferral
    {
        public void Discard() { }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

using Moq;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.TestDoubles;

/// <summary>Matchers and answers for the set-based device and patient-device lookups.</summary>
internal static class DeviceRepositoryMatchers
{
    /// <summary>A lookup of identities that includes this one.</summary>
    public static IReadOnlyCollection<(DeviceCategory Category, string Type, string Serial)> Naming(
        DeviceCategory category, string type, string serial) =>
        Match.Create<IReadOnlyCollection<(DeviceCategory Category, string Type, string Serial)>>(
            ids => ids.Contains((category, type, serial)));

    /// <summary>A lookup of device ids that includes this one.</summary>
    public static IReadOnlyCollection<Guid> Including(Guid deviceId) =>
        Match.Create<IReadOnlyCollection<Guid>>(ids => ids.Contains(deviceId));

    public static IReadOnlyList<Device> Found(Device? device) => device is null ? [] : [device];
}

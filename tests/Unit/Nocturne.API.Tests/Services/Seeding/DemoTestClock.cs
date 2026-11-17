using Nocturne.Services.Demo.Services;

namespace Nocturne.API.Tests.Services.Seeding;

internal static class DemoTestClock
{
    /// <summary>
    /// The generator tests compare generated times with the host's wall clock, so they lay the demo
    /// out in the host's zone.
    /// </summary>
    public static readonly DemoClock Host = new(TimeProvider.System, TimeZoneInfo.Local);
}

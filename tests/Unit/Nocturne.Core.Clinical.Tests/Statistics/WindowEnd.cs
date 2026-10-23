namespace Nocturne.Core.Clinical.Tests.Statistics;

internal static class WindowEnd
{
    internal static long Mills(DateTime end) =>
        new DateTimeOffset(DateTime.SpecifyKind(end, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
}

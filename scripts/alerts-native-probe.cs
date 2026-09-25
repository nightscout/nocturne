// scripts/alerts-native-probe.cs
//
// Loads the nocturne_alerts native library the way the API does and exits non-zero unless
// AlertsInterop.Probe accepts it. Run inside a built API image to prove the packed .so loads
// there (its glibc, its architecture), not just that the file exists.
//
// Usage:
//   dotnet publish scripts/alerts-native-probe.cs -o <dir>
//   find <dir> -name '*nocturne_alerts*' -delete    # the probe refuses to run beside a copy
//   docker run --rm --platform linux/amd64 --entrypoint dotnet -v <dir>:/probe:ro \
//     -e NOCTURNE_ALERTS_NATIVE_DIR=/app/runtimes/linux-x64/native <api-image> /probe/alerts-native-probe.dll

#:project ../src/Core/Nocturne.Core.Alerts.Native/Nocturne.Core.Alerts.Native.csproj
#:property PublishAot=false

using System.Runtime.InteropServices;
using Nocturne.Core.Alerts.Native;

// AlertsInterop falls back to copies under the app base, which would pass for the one under test.
var bundled = Directory.EnumerateFiles(AppContext.BaseDirectory, "*nocturne_alerts*", SearchOption.AllDirectories).ToList();
if (bundled.Count > 0)
{
    Console.Error.WriteLine($"remove the native library copies next to the probe first: {string.Join(", ", bundled)}");
    return 2;
}

var probe = AlertsInterop.Probe();
if (!probe.IsAvailable)
{
    Console.Error.WriteLine($"nocturne_alerts did not load on {RuntimeInformation.RuntimeIdentifier}: {probe.Failure}");
    return 1;
}

Console.WriteLine(
    $"nocturne_alerts {probe.Version} (tzdb {probe.TzdbVersion}) loads on {RuntimeInformation.RuntimeIdentifier}");
return 0;

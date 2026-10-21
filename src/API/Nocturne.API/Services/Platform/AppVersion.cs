using System.Reflection;

namespace Nocturne.API.Services.Platform;

/// <summary>
/// The release version of this build, as stamped by <c>-p:Version</c> at publish time.
/// </summary>
public static class AppVersion
{
    public static string Current { get; } = Resolve(typeof(AppVersion).Assembly);

    internal static string Resolve(Assembly assembly)
    {
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        return Resolve(informational, assembly.GetName().Version);
    }

    internal static string Resolve(string? informationalVersion, Version? assemblyVersion)
    {
        var withoutMetadata = informationalVersion?.Split('+')[0];

        if (!string.IsNullOrWhiteSpace(withoutMetadata))
            return withoutMetadata;

        return assemblyVersion?.ToString() ?? "unknown";
    }
}

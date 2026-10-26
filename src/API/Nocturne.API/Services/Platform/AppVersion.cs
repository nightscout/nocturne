using System.Reflection;

namespace Nocturne.API.Services.Platform;

/// <summary>
/// The release version of this build, as stamped by <c>-p:Version</c> at publish time.
/// </summary>
public static class AppVersion
{
    public static string Current { get; } = Resolve(typeof(AppVersion).Assembly);

    /// <summary>
    /// Whether this build was stamped from a release tag. A <c>develop</c> image and a local build
    /// carry the SDK's default <c>1.0.0</c>, which names no release.
    /// </summary>
    public static bool IsRelease { get; } = typeof(AppVersion).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Any(a => a.Key == "NocturneReleaseVersion" && !string.IsNullOrEmpty(a.Value));

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

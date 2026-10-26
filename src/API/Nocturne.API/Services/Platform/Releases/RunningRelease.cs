namespace Nocturne.API.Services.Platform.Releases;

/// <summary>
/// The version this process runs, and whether it is a release at all (<see cref="AppVersion.IsRelease"/>).
/// </summary>
public sealed record RunningRelease(string Version, bool IsRelease)
{
    public static RunningRelease FromAssembly() => new(AppVersion.Current, AppVersion.IsRelease);
}

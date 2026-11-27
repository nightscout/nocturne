using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Services.Alerts;

/// <summary>
/// Tells a failure of something an alert notice depends on from a defect: the database
/// (<see cref="StoreFailure"/>) or a provider a notice is sent through. A notice or resolution
/// path isolates the first kind, logs it and tries again on the next run; a defect reaches the
/// run that called it.
/// </summary>
internal static class ProviderFailure
{
    public static bool Is(Exception ex) =>
        StoreFailure.Is(ex) || ex is HttpRequestException or TimeoutException;
}

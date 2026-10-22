using Nocturne.API.Multitenancy;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Devices;

/// <summary>
/// Runs <see cref="ISensorSessionInferenceService"/> for a tenant in a DI scope of its own, with its
/// writes attributed to the system (<see cref="TenantJobScope"/>): a withdrawal
/// recorded as the user's would dismiss its gap for good.
/// </summary>
public interface ISensorSessionInferenceRunner
{
    Task RunAsync(TenantContext tenant, Func<ISensorSessionInferenceService, CancellationToken, Task> work, CancellationToken ct);
}

/// <inheritdoc />
public sealed class SensorSessionInferenceRunner(IServiceScopeFactory scopes) : ISensorSessionInferenceRunner
{
    private const string AuditEndpoint = "service:sensor-session-inference";

    /// <inheritdoc />
    public async Task RunAsync(
        TenantContext tenant, Func<ISensorSessionInferenceService, CancellationToken, Task> work, CancellationToken ct)
    {
        await using var scope = TenantJobScope.Open(scopes, tenant, AuditEndpoint);

        await work(scope.ServiceProvider.GetRequiredService<ISensorSessionInferenceService>(), ct);
    }
}

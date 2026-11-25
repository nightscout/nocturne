using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Identity;

/// <summary>
/// What the team announcers share: filing a notification for a site from a scope opened for it, and
/// keeping a failure to do so from reaching the write that has committed.
/// </summary>
public sealed class SiteNotices(ITenantRepository tenants, IServiceProvider services)
{
    /// <summary>
    /// Runs <paramref name="work"/> with the notifications of <paramref name="tenantId"/>, in a scope opened
    /// for that site as <paramref name="auditEndpoint"/>. Does nothing when the site is gone.
    /// </summary>
    public async Task RunAsync(Guid tenantId, string auditEndpoint, Func<IInAppNotificationService, Task> work)
    {
        if (await tenants.GetAsync(tenantId, CancellationToken.None) is not { } tenant)
            return;

        await using var scope = TenantJobScope.Open(
            services,
            new TenantContext(tenant.Id, tenant.Slug, tenant.DisplayName, tenant.IsActive, tenant.IsDemo),
            auditEndpoint);
        await work(scope.ServiceProvider.GetRequiredService<IInAppNotificationService>());
    }

    /// <summary>
    /// Runs <paramref name="work"/>, handing a store failure, a cancellation or an invalid operation to
    /// <paramref name="failed"/> instead of throwing: the write the notice is for has committed.
    /// </summary>
    public static async Task BestEffortAsync(Func<Task> work, Action<Exception> failed)
    {
        try
        {
            await work();
        }
        catch (Exception ex) when (StoreFailure.Is(ex))
        {
            failed(ex);
        }
        catch (OperationCanceledException ex)
        {
            failed(ex);
        }
        catch (InvalidOperationException ex)
        {
            failed(ex);
        }
    }
}

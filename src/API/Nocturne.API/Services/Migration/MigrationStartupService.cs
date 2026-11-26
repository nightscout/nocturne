using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.Connectors.Nightscout.Services;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Migration;

/// <summary>
/// Hosted service that checks for a pending migration on startup (via the <c>MIGRATION_MODE</c>
/// environment variable) and, when the instance's only tenant has no completed run from the
/// configured source, gives it an admin in-app notification prompting its admin to start the
/// migration. The configured source is the operator's, so the tenant of an instance with others is
/// not invited to import it (<see cref="ConfiguredMigrationSource"/>).
/// </summary>
/// <seealso cref="IMigrationJobService"/>
public class MigrationStartupService : IHostedService
{
    private const string AuditEndpoint = "service:migration-startup";

    private readonly IServiceProvider _serviceProvider;
    private readonly ActiveTenantSnapshot _activeTenants;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MigrationStartupService> _logger;

    public MigrationStartupService(
        IServiceProvider serviceProvider,
        ActiveTenantSnapshot activeTenants,
        IConfiguration configuration,
        ILogger<MigrationStartupService> logger)
    {
        _serviceProvider = serviceProvider;
        _activeTenants = activeTenants;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (ConfiguredMigrationSource.Read(_configuration) is not { } configured)
        {
            _logger.LogDebug("No MIGRATION_MODE env var set, skipping migration startup check");
            return;
        }

        _logger.LogInformation("MIGRATION_MODE is set to {Mode}, checking for pending migration", configured.Mode);

        if (GetSource(configured) is not { } source)
        {
            _logger.LogWarning("Could not determine source identifier for migration mode {Mode}", configured.Mode);
            return;
        }

        IReadOnlyList<ActiveTenant> tenants;
        try
        {
            tenants = await _activeTenants.GetAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing tenants for the pending migration check");
            return;
        }

        foreach (var tenant in tenants)
        {
            try
            {
                await NotifyIfPendingAsync(tenant, source, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking for pending migration for tenant {TenantId}", tenant.Id);
            }
        }
    }

    private async Task NotifyIfPendingAsync(
        ActiveTenant tenant,
        (string Identifier, string Label) source,
        CancellationToken cancellationToken)
    {
        await using var scope = TenantJobScope.Open(
            _serviceProvider,
            new TenantContext(tenant.Id, tenant.Slug, tenant.DisplayName, true, IsDemo: false),
            AuditEndpoint);

        if (!await InstanceStanding.IsOnlyTenantAsync(
                scope.ServiceProvider.GetRequiredService<ITenantRepository>(), tenant.Id, cancellationToken))
        {
            _logger.LogDebug(
                "Tenant {TenantId} is not the instance's only tenant, no migration notification", tenant.Id);
            return;
        }

        await using var dbContext = await scope.ServiceProvider
            .GetRequiredService<INocturneDbContextFactory>()
            .CreateAsync(cancellationToken);

        var hasCompletedRun = await dbContext.MigrationRuns
            .AnyAsync(r => r.TenantId == tenant.Id
                       && r.Source.SourceIdentifier == source.Identifier
                       && r.State == "Completed", cancellationToken);

        if (hasCompletedRun)
        {
            _logger.LogDebug(
                "Completed migration run found for tenant {TenantId} and source {Source}, no notification needed",
                tenant.Id, source.Label);
            return;
        }

        _logger.LogInformation(
            "No completed migration found for tenant {TenantId} and source {Source}, creating admin notification",
            tenant.Id, source.Label);

        await scope.ServiceProvider.GetRequiredService<INotificationV1Service>().AddAdminNotificationAsync(new AdminNotification
        {
            Title = "Migration Available",
            Message = "Nightscout migration configured - click to import your data",
            Persistent = false // Will auto-cleanup after 9 hours
        }, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// The configured source's identifier, byte-identical to the one
    /// <c>MigrationJob.UpsertSourceAsync</c> stores, and a label that may be logged.
    /// </summary>
    private static (string Identifier, string Label)? GetSource(ConfiguredMigrationSource configured)
    {
        if (configured.Mode == MigrationMode.MongoDb)
        {
            if (configured.MongoConnectionString is not { } connectionString)
                return null;

            var digest = MigrationJob.MongoSourceIdentifier(connectionString);
            return (digest, digest);
        }

        return configured.NightscoutUrl is { } url
            ? (MigrationJob.ApiSourceIdentifier(url), NightscoutAddress.Shown(url))
            : null;
    }
}

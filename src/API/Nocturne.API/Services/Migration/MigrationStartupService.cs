using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.Connectors.Nightscout.Services;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.API.Services.Migration;

/// <summary>
/// Hosted service that checks for a pending MongoDB migration on startup (via the
/// <c>MIGRATION_MODE</c> environment variable) and gives each active tenant without a completed run
/// from the configured source an admin in-app notification, prompting its admin to initiate the
/// migration via the portal.
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
        var migrationMode = _configuration["MIGRATION_MODE"];

        if (string.IsNullOrEmpty(migrationMode))
        {
            _logger.LogDebug("No MIGRATION_MODE env var set, skipping migration startup check");
            return;
        }

        _logger.LogInformation("MIGRATION_MODE is set to {Mode}, checking for pending migration", migrationMode);

        if (GetSource(migrationMode) is not { } source)
        {
            _logger.LogWarning("Could not determine source identifier for migration mode {Mode}", migrationMode);
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
    private (string Identifier, string Label)? GetSource(string migrationMode)
    {
        if (migrationMode.Equals("MongoDb", StringComparison.OrdinalIgnoreCase))
        {
            var connectionString = _configuration["MIGRATION_MONGO_CONNECTION_STRING"];
            if (string.IsNullOrEmpty(connectionString))
                return null;

            var digest = MigrationJob.MongoSourceIdentifier(connectionString);
            return (digest, digest);
        }

        var url = _configuration["MIGRATION_NS_URL"];
        return string.IsNullOrEmpty(url)
            ? null
            : (MigrationJob.ApiSourceIdentifier(url), NightscoutAddress.Masked(url));
    }
}

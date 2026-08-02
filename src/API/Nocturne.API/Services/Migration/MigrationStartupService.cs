using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Contracts.Notifications;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Services.Migration;

/// <summary>
/// Hosted service that checks for a pending MongoDB migration on startup (via the
/// <c>MIGRATION_MODE</c> environment variable) and creates an admin in-app notification if one is
/// detected, prompting the admin to initiate the migration via the portal.
/// </summary>
/// <seealso cref="IMigrationJobService"/>
public class MigrationStartupService : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MigrationStartupService> _logger;

    public MigrationStartupService(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILogger<MigrationStartupService> logger)
    {
        _serviceProvider = serviceProvider;
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

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();
            var notificationService = scope.ServiceProvider.GetRequiredService<INotificationV1Service>();

            // Get the source identifier based on mode
            var sourceIdentifier = GetSourceIdentifier(migrationMode);
            if (string.IsNullOrEmpty(sourceIdentifier))
            {
                _logger.LogWarning("Could not determine source identifier for migration mode {Mode}", migrationMode);
                return;
            }

            // Check if any successful migration runs exist for this source
            var hasCompletedRun = await dbContext.MigrationRuns
                .AnyAsync(r => r.Source.SourceIdentifier == sourceIdentifier
                           && r.State == "Completed", cancellationToken);

            if (hasCompletedRun)
            {
                _logger.LogDebug("Completed migration run found for source {Source}, no notification needed", sourceIdentifier);
                return;
            }

            // Create admin notification for pending migration
            _logger.LogInformation("No completed migration found for source {Source}, creating admin notification", sourceIdentifier);

            await notificationService.AddAdminNotificationAsync(new AdminNotification
            {
                Title = "Migration Available",
                Message = "Nightscout migration configured - click to import your data",
                Persistent = false // Will auto-cleanup after 9 hours
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking for pending migration on startup");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private string? GetSourceIdentifier(string migrationMode)
    {
        // Must produce byte-identical identifiers to MigrationJob.UpsertSourceAsync, or the
        // "was this source ever migrated?" check can never match and the pending-migration
        // notification reappears on every boot.
        if (migrationMode.Equals("MongoDb", StringComparison.OrdinalIgnoreCase))
        {
            var connectionString = _configuration["MIGRATION_MONGO_CONNECTION_STRING"];
            return string.IsNullOrEmpty(connectionString)
                ? null
                : MigrationJob.MongoSourceIdentifier(connectionString);
        }

        var url = _configuration["MIGRATION_NS_URL"];
        return string.IsNullOrEmpty(url) ? null : MigrationJob.ApiSourceIdentifier(url);
    }
}

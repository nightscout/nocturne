using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Background service that periodically cleans up expired OAuth device codes
/// and authorization codes to prevent database bloat.
/// Runs every hour and removes codes that expired more than 1 hour ago.
/// </summary>
public class OAuthCodeCleanupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OAuthCodeCleanupService> _logger;

    /// <summary>
    /// Interval between cleanup runs (1 hour)
    /// </summary>
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);

    /// <summary>
    /// Buffer period after expiry before deletion (1 hour).
    /// Codes are only deleted once they have been expired for at least this long,
    /// giving clients a grace window to complete any in-flight exchanges.
    /// </summary>
    private static readonly TimeSpan RetentionBuffer = TimeSpan.FromHours(1);

    /// <summary>
    /// Initializes a new instance of the <see cref="OAuthCodeCleanupService"/> class.
    /// </summary>
    /// <param name="scopeFactory">Service scope factory for creating scoped services</param>
    /// <param name="logger">Logger</param>
    public OAuthCodeCleanupService(
        IServiceScopeFactory scopeFactory,
        ILogger<OAuthCodeCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Execute the background service
    /// </summary>
    /// <param name="stoppingToken">Stopping token</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OAuth code cleanup service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CleanupInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await CleanupExpiredCodesAsync(stoppingToken);
            }
            catch (DbUpdateException dbEx)
            {
                _logger.LogError(dbEx, "Database update error during OAuth code cleanup");
            }
            catch (DbUpdateConcurrencyException dbCx)
            {
                _logger.LogError(dbCx, "Database concurrency error during OAuth code cleanup");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during OAuth code cleanup");
                throw;
            }
        }

        _logger.LogInformation("OAuth code cleanup service stopped");
    }

    /// <summary>
    /// Delete device codes and authorization codes that expired before the retention cutoff.
    /// Uses EF Core <c>ExecuteDeleteAsync</c> for efficient bulk deletion without loading entities.
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    private async Task CleanupExpiredCodesAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();

        var cutoff = DateTime.UtcNow.Subtract(RetentionBuffer);

        var deletedDeviceCodes = await db.OAuthDeviceCodes
            .Where(d => d.ExpiresAt < cutoff)
            .ExecuteDeleteAsync(ct);

        var deletedAuthCodes = await db.OAuthAuthorizationCodes
            .Where(c => c.ExpiresAt < cutoff)
            .ExecuteDeleteAsync(ct);

        if (deletedDeviceCodes > 0 || deletedAuthCodes > 0)
        {
            _logger.LogInformation(
                "OAuth code cleanup: removed {DeviceCodes} device codes and {AuthCodes} authorization codes",
                deletedDeviceCodes, deletedAuthCodes);
        }
    }

    /// <summary>
    /// Stop the background service
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("OAuth code cleanup service is stopping");
        await base.StopAsync(cancellationToken);
    }
}

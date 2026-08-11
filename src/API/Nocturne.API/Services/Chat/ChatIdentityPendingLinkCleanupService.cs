namespace Nocturne.API.Services.Chat;

/// <summary>
/// Periodically deletes expired chat identity pending link tokens so
/// <c>chat_identity_pending_links</c> does not grow without bound.
/// </summary>
/// <remarks>
/// <para>
/// A pending link token lives for <see cref="ChatIdentityPendingLinkService.TokenLifetime"/> and is
/// deleted when it is consumed, so only abandoned link flows leave rows behind. The sweep runs
/// hourly; nothing depends on it running promptly because
/// <see cref="ChatIdentityPendingLinkService.TryConsumeAsync"/> already rejects an expired token.
/// </para>
/// <para>
/// <c>chat_identity_pending_links</c> lives in the public schema, is not tenant-scoped and carries
/// no RLS policy — the sweep is a single cross-tenant delete, not a per-tenant loop, and needs no
/// tenant GUC on the connection.
/// </para>
/// </remarks>
internal sealed class ChatIdentityPendingLinkCleanupService(
    IServiceProvider serviceProvider,
    ILogger<ChatIdentityPendingLinkCleanupService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>
    /// Delay before the first sweep, so the sweep does not open a connection while the host is still
    /// starting — the API also boots for OpenAPI generation, where it never migrates and may have no
    /// database to reach. Settable so tests do not have to wait it out.
    /// </summary>
    internal TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(10);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Chat identity pending link cleanup service started");

        try
        {
            await Task.Delay(InitialDelay, stoppingToken);

            using var timer = new PeriodicTimer(Interval);

            do
            {
                try
                {
                    await SweepAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Error during chat identity pending link cleanup");
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Chat identity pending link cleanup service stopping");
        }
    }

    /// <summary>
    /// Runs one cleanup pass in its own DI scope.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of expired rows deleted.</returns>
    internal async Task<int> SweepAsync(CancellationToken ct)
    {
        using var scope = serviceProvider.CreateScope();
        var pendingLinks = scope.ServiceProvider.GetRequiredService<ChatIdentityPendingLinkService>();
        return await pendingLinks.CleanupExpiredAsync(ct);
    }
}

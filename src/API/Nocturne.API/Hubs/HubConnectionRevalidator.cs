using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

namespace Nocturne.API.Hubs;

/// <summary>
/// Proves every authorized hub connection's credential again each <see cref="Interval"/> through
/// <see cref="HubCredentialVerifier"/>, and closes a connection whose credential no longer covers
/// what it connected with (<see cref="HubAuthorization.IsCoveredBy"/>): a deactivated or deleted
/// tenant, whose connections include the realtime bridge's, a removed member, a revoked
/// grant or guest link, a deactivated subject, a session logged out, narrowed scopes or a new
/// history clamp. An <see cref="OverviewHub"/> connection is closed once any tenant it receives is
/// out of its reach.
/// </summary>
/// <remarks>
/// A connection is closed rather than narrowed in place because the groups it joined were chosen
/// from the old credential. Closing makes the client reconnect, and its new connection joins only
/// what its current credential allows. Each node re-checks the connections it holds, so this needs
/// no backplane.
/// <para>
/// The sweep runs off the connection's own calls, so it reads only what the connection holds, never
/// its upgrade request: the credential (<see cref="HubAuthorizationState.Granted"/>), which
/// <see cref="HubAuthorizationFilter"/> has resolved onto the connection before it tracks it, and the
/// tenant of the reader held since admission (<see cref="HubConnectionReader.Held"/>).
/// </para>
/// </remarks>
public sealed class HubConnectionRevalidator(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<HubConnectionRevalidator> logger) : BackgroundService
{
    /// <summary>How long a connection may outlive the withdrawal of its credential.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, HubCallerContext> _connections = new();

    /// <summary>Starts re-checking <paramref name="context"/>'s credential.</summary>
    public void Track(HubCallerContext context) => _connections.TryAdd(context.ConnectionId, context);

    /// <summary>Stops re-checking a connection that has closed.</summary>
    public void Forget(HubCallerContext context) => _connections.TryRemove(context.ConnectionId, out _);

    /// <summary>Re-checks every tracked connection once, closing each that fails.</summary>
    /// <remarks>
    /// Connections are checked one after another: the verifier memoizes per sweep, so connections
    /// sharing a subject, grant or tenant cost one read between them, and its scoped services are
    /// never used concurrently. A connection whose check throws is kept until the next sweep.
    /// </remarks>
    public async Task RevalidateAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var verifier = new HubCredentialVerifier(scope.ServiceProvider, timeProvider);

        foreach (var context in _connections.Values)
        {
            try
            {
                if (context.ConnectionAborted.IsCancellationRequested)
                {
                    Forget(context);
                    continue;
                }

                if (await StillCoveredAsync(verifier, context))
                    continue;

                logger.LogInformation(
                    "Closing hub connection {ConnectionId}: its credential no longer authorizes it",
                    context.ConnectionId);
                Forget(context);
                context.Abort();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not re-check hub connection {ConnectionId}", context.ConnectionId);
            }
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var started = timeProvider.GetTimestamp();
                    await RevalidateAsync(stoppingToken);
                    var elapsed = timeProvider.GetElapsedTime(started);
                    if (elapsed > Interval)
                    {
                        logger.LogWarning(
                            "Hub connection re-check sweep of {Count} connections took {Elapsed}, longer than its {Interval} interval",
                            _connections.Count, elapsed, Interval);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "Hub connection re-check sweep failed");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private static async Task<bool> StillCoveredAsync(HubCredentialVerifier verifier, HubCallerContext context)
    {
        if (HubAuthorizationState.Overview(context) is { } overview && !await verifier.StillHoldsAsync(overview))
            return false;

        if (HubAuthorizationState.Granted(context) is not { } granted)
            return true;

        return HubConnectionReader.Held(context)?.Tenant is { } tenant
            && granted.IsCoveredBy(await verifier.ProveAsync(granted, tenant));
    }
}

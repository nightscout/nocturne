namespace Nocturne.Core.Models.Connectors;

/// <summary>
/// Where a connector's syncing stands, from its stored health. Failing is the state the connector
/// pages show as Error, and never synced the one they show as Configured.
/// </summary>
public static class ConnectorSyncStatus
{
    public const string Healthy = "healthy";
    public const string Failing = "failing";
    public const string Stale = "stale";
    public const string NeverSynced = "never_synced";

    /// <summary>
    /// Failing while the last sync left an error; otherwise never synced without a successful sync,
    /// stale when that sync is older than <paramref name="staleAfter"/>, and healthy when it is not.
    /// </summary>
    public static string Of(
        bool isHealthy, DateTime? lastErrorAt, DateTime? lastSuccessfulSync, DateTime now, TimeSpan staleAfter)
    {
        if (!isHealthy && lastErrorAt is not null)
            return Failing;
        if (lastSuccessfulSync is not { } synced)
            return NeverSynced;
        return now - synced > staleAfter ? Stale : Healthy;
    }
}

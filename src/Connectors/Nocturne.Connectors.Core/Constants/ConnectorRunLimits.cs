namespace Nocturne.Connectors.Core.Constants;

public static class ConnectorRunLimits
{
    /// <summary>
    ///     The longest one tenant's background sync may run before it is cancelled. Bounds how long one
    ///     stuck or failing tenant — a hung network call, an auth-retry storm against bad credentials —
    ///     can hold a scheduler slot and a budget slot.
    /// </summary>
    public static readonly TimeSpan PerTenantSyncTimeout = TimeSpan.FromMinutes(3);

    /// <summary>
    ///     How far into a background run held to <paramref name="timeout"/> a backfill crawl may still
    ///     publish another page. A run the timeout cuts off fails, and the scheduler backs a failing
    ///     connector off, so backfill stops at half of it and leaves the rest to the types not yet
    ///     crawled.
    /// </summary>
    public static TimeSpan BackfillBudgetFor(TimeSpan timeout) => timeout / 2;
}

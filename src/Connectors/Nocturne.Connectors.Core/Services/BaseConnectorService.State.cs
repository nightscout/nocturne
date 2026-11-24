using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Contracts.Connectors;

namespace Nocturne.Connectors.Core.Services;

public abstract partial class BaseConnectorService<TConfig>
{
    /// <summary>The run's copy of the resume state, read once per run and kept in step with every write.</summary>
    private Dictionary<string, DateTime>? _resumeState;

    /// <summary>
    ///     The collections a reset run has crawled, each of whose reach mark it has cleared; null
    ///     outside a reset run.
    /// </summary>
    private HashSet<string>? _resetCrawled;

    /// <summary>The reset request of a run that has not signed in yet; null outside a reset run and once applied.</summary>
    private SyncRequest? _pendingReset;

    private IConnectorStateStore? State => _publisher is { IsAvailable: true } p ? p.State : null;

    private async Task<Dictionary<string, DateTime>> ResumeStateAsync(CancellationToken cancellationToken) =>
        _resumeState ??= State is { } state
            ? new Dictionary<string, DateTime>(
                await state.ResumeAsync(ConnectorSource, cancellationToken) ?? new Dictionary<string, DateTime>())
            : [];

    /// <remarks>
    ///     The crawl writes with <see cref="CancellationToken.None"/>: <c>CatchUpAsync</c> and
    ///     <c>ResumeGapAsync</c> record what they owe from their <c>catch</c>, where the run's token
    ///     may already be cancelled and would throw before the owed range was written.
    /// </remarks>
    private async Task WriteResumeStateAsync(
        Dictionary<string, DateTime?> changes, CancellationToken cancellationToken)
    {
        if (changes.Count == 0)
            return;

        var resume = await ResumeStateAsync(cancellationToken);
        if (State is { } state)
            await state.SetResumeAsync(ConnectorSource, changes, cancellationToken);

        foreach (var (key, at) in changes)
        {
            if (at is { } time)
                resume[key] = time;
            else
                resume.Remove(key);
        }
    }

    /// <summary>
    ///     The time this connector stored under <paramref name="key"/> in its resume state
    ///     (<see cref="ConnectorStateSlots.Resume"/>), or <c>null</c>. Declare the key in
    ///     <see cref="ResumeStamps"/> for a cursor reset to clear it.
    /// </summary>
    protected async Task<DateTime?> ResumeStampAsync(string key, CancellationToken cancellationToken) =>
        (await ResumeStateAsync(cancellationToken)).TryGetValue(key, out var at) ? at : null;

    /// <summary>Stores <paramref name="at"/> under <paramref name="key"/> in the resume state, or removes the key when it is <c>null</c>.</summary>
    protected Task SetResumeStampAsync(string key, DateTime? at, CancellationToken cancellationToken) =>
        WriteResumeStateAsync(new() { [key] = at }, cancellationToken);

    /// <summary>
    ///     Applies <paramref name="changes"/> to the resume state in one write, as <see cref="SetResumeStampAsync"/>
    ///     does each, so keys that only mean something together are never stored one without the other.
    /// </summary>
    protected Task SetResumeStampsAsync(
        IReadOnlyDictionary<string, DateTime?> changes, CancellationToken cancellationToken) =>
        WriteResumeStateAsync(new Dictionary<string, DateTime?>(changes), cancellationToken);

    /// <summary>
    ///     The stamps this connector keeps through <see cref="SetResumeStampAsync"/>, each with the data
    ///     types whose sync it schedules. A cursor reset clears a stamp only when it re-pulls every one
    ///     of those types the tenant has switched on (<see cref="ResetClears"/>).
    /// </summary>
    protected virtual IReadOnlyDictionary<string, IReadOnlyCollection<SyncDataType>> ResumeStamps { get; } =
        new Dictionary<string, IReadOnlyCollection<SyncDataType>>();

    /// <summary>
    ///     The value this connector stored under <paramref name="key"/> in its vendor state
    ///     (<see cref="ConnectorStateSlots.Vendor"/>), or the default when none is stored or it no
    ///     longer reads as <typeparamref name="T"/>.
    /// </summary>
    protected async Task<T?> VendorStateAsync<T>(string key, CancellationToken cancellationToken)
    {
        if (State is not { } state)
            return default;

        var stored = await state.VendorAsync(ConnectorSource, key, cancellationToken);
        try
        {
            return stored is null ? default : stored.Deserialize<T>();
        }
        catch (JsonException)
        {
            return default;
        }
    }

    /// <summary>Stores <paramref name="value"/> under <paramref name="key"/> in the vendor state, or removes the key when it is the default.</summary>
    protected Task SetVendorStateAsync<T>(string key, T? value, CancellationToken cancellationToken) =>
        State is { } state
            ? state.SetVendorAsync(
                ConnectorSource, key, value is null ? null : JsonSerializer.SerializeToNode(value), cancellationToken)
            : Task.CompletedTask;

    /// <summary>
    ///     Runs <paramref name="read"/> of connector state that the run can do without. A store that is
    ///     down (<see cref="ConnectorStateUnavailableException"/>) or that times out
    ///     (<see cref="ConnectorFaults.IsTimeout"/>) logs a warning and yields <paramref name="fallback"/>.
    ///     Any other exception, and a cancellation <paramref name="cancellationToken"/> asked for, fails the run.
    /// </summary>
    /// <param name="description">What the read was for, as the end of "Could not ...".</param>
    protected async Task<T> ReadStateBestEffortAsync<T>(
        string description, T fallback, Func<Task<T>> read, CancellationToken cancellationToken)
    {
        try
        {
            return await read();
        }
        catch (Exception ex) when (ex is ConnectorStateUnavailableException
                                   || ConnectorFaults.IsTimeout(ex, cancellationToken))
        {
            _logger.LogWarning(ex, "[{ConnectorSource}] Could not {Description}", ConnectorSource, description);
            return fallback;
        }
    }

    /// <summary>
    ///     Runs <paramref name="write"/> of connector state that the run can do without, as
    ///     <see cref="ReadStateBestEffortAsync{T}"/> does a read.
    /// </summary>
    /// <param name="description">What the write was for, as the end of "Could not ...".</param>
    /// <returns>Whether the write was made.</returns>
    protected async Task<bool> WriteStateBestEffortAsync(
        string description, Func<Task> write, CancellationToken cancellationToken)
    {
        try
        {
            await write();
            return true;
        }
        catch (Exception ex) when (ex is ConnectorStateUnavailableException
                                   || ConnectorFaults.IsTimeout(ex, cancellationToken))
        {
            _logger.LogWarning(ex, "[{ConnectorSource}] Could not {Description}", ConnectorSource, description);
            return false;
        }
    }

    /// <summary>
    ///     Whether a cursor reset that re-pulls every data type the connector fetches also clears its
    ///     vendor state. Off by default: vendor state such as a fingerprint of what was last published
    ///     is not a position, and clearing it can only cost a repeat.
    /// </summary>
    protected virtual bool ResetClearsVendorState => false;

    /// <summary>What a resume key records, which decides whether a cursor reset clears it.</summary>
    internal enum ResumeKeyKind
    {
        /// <summary>History a crawl still owes: a collection's low-water mark and its gap.</summary>
        OwedMark,

        /// <summary>How far a collection's crawls reached.</summary>
        ReachMark,

        /// <summary>A connector's own stamp, declared in <see cref="ResumeStamps"/>.</summary>
        Stamp,
    }

    /// <summary>
    ///     Whether a cursor reset clears a resume key of <paramref name="kind"/>.
    ///     <paramref name="repulled"/> says whether the reset run re-pulls what the key governs: the
    ///     collection a mark belongs to when the run crawls it, or every type a stamp governs.
    /// </summary>
    /// <remarks>
    ///     An owed mark always stays, so a reset never gives up history the store does not hold, and
    ///     the runs after it go on crawling below the mark as they would have without the reset.
    /// </remarks>
    internal static bool ResetClears(ResumeKeyKind kind, bool repulled) =>
        repulled && kind is not ResumeKeyKind.OwedMark;

    /// <summary>
    ///     Whether <see cref="EnsureAuthenticatedAsync"/> is this connector's sign-in, so the run has
    ///     signed in once it answers <c>true</c>. A connector that signs in inside its sync leaves this
    ///     off and calls <see cref="SignedInAsync"/> once its source has accepted the credential.
    /// </summary>
    protected virtual bool SignsInBeforeSync => false;

    /// <summary>
    ///     Marks the run as signed in. On a reset run this is where the reset forgets state
    ///     (<see cref="ResetStateAsync"/>), so a reset whose sign-in is refused, or that never calls
    ///     this, clears nothing. Later calls in the same run do nothing.
    /// </summary>
    protected async Task SignedInAsync(TConfig config, CancellationToken cancellationToken)
    {
        if (_pendingReset is not { } request)
            return;

        _pendingReset = null;
        await ResetStateAsync(request, config, cancellationToken);
    }

    /// <summary>
    ///     Forgets the stamps <paramref name="request"/> re-pulls, and the vendor state when
    ///     <see cref="ResetClearsVendorState"/> says so. The crawl clears each collection's marks as
    ///     it reaches it (<see cref="ForgetCrawlForResetAsync"/>).
    /// </summary>
    /// <remarks>
    ///     A set of governed types none of which the tenant has switched on is not re-pulled, so a
    ///     reset of a connector with every type off clears nothing.
    /// </remarks>
    private async Task ResetStateAsync(SyncRequest request, TConfig config, CancellationToken cancellationToken)
    {
        _resetCrawled = [];
        if (State is not { } state)
            return;

        var active = ResolveActiveTypes(request, config);
        var enabled = config.GetEnabledDataTypes(SupportedDataTypes).ToHashSet();
        bool Repulls(IEnumerable<SyncDataType> types)
        {
            var governed = types.Where(enabled.Contains).ToList();
            return governed.Count > 0 && governed.All(active.Contains);
        }

        var stored = await ResumeStateAsync(cancellationToken);
        await WriteResumeStateAsync(
            ResumeStamps
                .Where(stamp => stored.ContainsKey(stamp.Key) && ResetClears(ResumeKeyKind.Stamp, Repulls(stamp.Value)))
                .ToDictionary(stamp => stamp.Key, _ => (DateTime?)null),
            cancellationToken);

        if (ResetClearsVendorState && Repulls(SupportedDataTypes))
            await state.ClearAsync(ConnectorSource, ConnectorStateSlots.Vendor, cancellationToken);
    }
}

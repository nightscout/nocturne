using System.Collections.Concurrent;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;

namespace Nocturne.Infrastructure.Data.Tests.V4Goldens;

/// <summary>
/// Settable <see cref="ITenantAccessor"/> for golden tests. The fixture sets the current tenant
/// before a scenario resolves a scope; the scoped <see cref="NocturneDbContext"/> and
/// <c>TenantDbContextFactory</c> both read it to stamp the RLS tenant carrier.
/// </summary>
internal sealed class TestTenantAccessor : ITenantAccessor
{
    public Guid TenantId => Context?.TenantId ?? Guid.Empty;
    public bool IsResolved => Context is not null;
    public TenantContext? Context { get; private set; }
    public void SetTenant(TenantContext context) => Context = context;
}

/// <summary>
/// System-attributed <see cref="IAuditContext"/> (no human actor), matching how connectors ingest —
/// so audited soft-deletes in the dedup paths are treated as system-initiated, as in production.
/// </summary>
internal sealed class SystemAuditContext : IAuditContext
{
    public Guid? SubjectId => null;
    public string? SubjectName => null;
    public string? AuthType => null;
    public string? IpAddress => null;
    public Guid? TokenId => null;
    public string? CorrelationId => null;
    public string? Endpoint => null;
    public bool IsSystem => true;
}

/// <summary>
/// One recorded fan-out from the V4 repository chokepoint: the broadcast <paramref name="Kind"/>
/// (<c>created</c>/<c>updated</c>/<c>deleted</c>), the model <paramref name="ModelType"/>, the item
/// <paramref name="Count"/>, and the deleted record <paramref name="Ids"/> (empty for create/update).
/// </summary>
public sealed record BroadcastEntry(string Kind, Type ModelType, int Count, IReadOnlyList<Guid> Ids);

/// <summary>
/// Thread-safe singleton collector that the per-type <see cref="CapturingV4RecordBroadcaster{TModel}"/>
/// instances record into. Lets a golden assert exactly which broadcasts the chokepoint fired (and that
/// backfill / no-material-change writes fire none). Additive: goldens that don't read it are unaffected.
/// </summary>
public sealed class BroadcastCapture
{
    private readonly ConcurrentQueue<BroadcastEntry> _entries = new();

    /// <summary>Record a single broadcast fan-out.</summary>
    public void Add(BroadcastEntry entry) => _entries.Enqueue(entry);

    /// <summary>Drop all captured entries — call at the start of each scenario.</summary>
    public void Clear() => _entries.Clear();

    /// <summary>Point-in-time snapshot of the captured entries in fire order.</summary>
    public IReadOnlyList<BroadcastEntry> Snapshot() => _entries.ToArray();
}

/// <summary>
/// Capturing <see cref="IV4RecordBroadcaster{TModel}"/> that records every fan-out into the shared
/// <see cref="BroadcastCapture"/> instead of touching SignalR. Registered as an open generic so every V4
/// repository resolves one, exercising the real <c>V4RepositoryBase</c> origin gate end-to-end.
/// </summary>
internal sealed class CapturingV4RecordBroadcaster<TModel>(BroadcastCapture capture)
    : IV4RecordBroadcaster<TModel> where TModel : class
{
    public Task BroadcastCreatedAsync(IReadOnlyList<TModel> items, CancellationToken ct = default)
    {
        if (items.Count > 0) capture.Add(new BroadcastEntry("created", typeof(TModel), items.Count, []));
        return Task.CompletedTask;
    }

    public Task BroadcastUpdatedAsync(IReadOnlyList<TModel> items, CancellationToken ct = default)
    {
        if (items.Count > 0) capture.Add(new BroadcastEntry("updated", typeof(TModel), items.Count, []));
        return Task.CompletedTask;
    }

    public Task BroadcastDeletedAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count > 0) capture.Add(new BroadcastEntry("deleted", typeof(TModel), ids.Count, ids));
        return Task.CompletedTask;
    }
}

/// <summary>
/// One recorded legacy-<see cref="Entry"/> projection fired by the chokepoint: the projection
/// <paramref name="Kind"/> (<c>created</c>/<c>updated</c>/<c>deleted</c>) and the projected
/// <paramref name="Entries"/> (a single-element list for the update/delete paths).
/// </summary>
public sealed record EntryProjectionEvent(string Kind, IReadOnlyList<Entry> Entries);

/// <summary>
/// Thread-safe singleton collector that <see cref="CapturingEntrySink"/> records into. Lets a golden
/// assert exactly which legacy <c>entries</c> projections the glucose-family chokepoint fired (sgv/mbg/cal
/// shapes, Live-only, and that non-glucose / backfill writes fire none). Additive: goldens that don't read
/// it are unaffected.
/// </summary>
public sealed class EntryCapture
{
    private readonly ConcurrentQueue<EntryProjectionEvent> _events = new();

    /// <summary>Record a single legacy-entry projection fan-out.</summary>
    public void Add(EntryProjectionEvent ev) => _events.Enqueue(ev);

    /// <summary>Drop all captured events — call at the start of each scenario.</summary>
    public void Clear() => _events.Clear();

    /// <summary>Point-in-time snapshot of the captured events in fire order.</summary>
    public IReadOnlyList<EntryProjectionEvent> Snapshot() => _events.ToArray();
}

/// <summary>
/// Capturing <see cref="IDataEventSink{Entry}"/> that records the legacy-entry projections the V4
/// chokepoint fires (instead of broadcasting them to V1/V3 clients). Resolved as the optional
/// <c>IDataEventSink&lt;Entry&gt;</c> the glucose repos accept, so <c>RaiseEntriesProjectionAsync</c> fires
/// into <see cref="EntryCapture"/> end-to-end. Interface methods not used by the projection path keep their
/// default no-op implementations.
/// </summary>
internal sealed class CapturingEntrySink(EntryCapture capture) : IDataEventSink<Entry>
{
    public Task OnCreatedAsync(IReadOnlyList<Entry> items, CancellationToken ct = default)
    {
        if (items.Count > 0) capture.Add(new EntryProjectionEvent("created", items));
        return Task.CompletedTask;
    }

    public Task OnUpdatedAsync(Entry item, CancellationToken ct = default)
    {
        capture.Add(new EntryProjectionEvent("updated", [item]));
        return Task.CompletedTask;
    }

    public Task OnDeletedAsync(Entry? item, CancellationToken ct = default)
    {
        if (item is not null) capture.Add(new EntryProjectionEvent("deleted", [item]));
        return Task.CompletedTask;
    }
}

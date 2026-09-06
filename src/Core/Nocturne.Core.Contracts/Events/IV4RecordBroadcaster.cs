namespace Nocturne.Core.Contracts.Events;

/// <summary>
/// Driven port for broadcasting native V4 record shapes to modern realtime subscribers (the desktop
/// companion + Prelude) over the four V4 categories (<c>glucose</c>/<c>care</c>/<c>device</c>/<c>therapy</c>).
/// </summary>
/// <typeparam name="TModel">The V4 domain model type whose writes are broadcast.</typeparam>
/// <remarks>
/// Fired from the V4 repository chokepoint, for live writes only — the chokepoint gates on
/// <see cref="V4.WriteOrigin"/> and never calls this for backfill imports. All methods default to no-op
/// so a type with no category mapping (e.g. the dormant <c>therapy</c> schedules) broadcasts nothing.
/// The payload carries a <c>recordType</c> discriminator so per-category subscribers filter per-type
/// client-side. Additive to the legacy v1 <c>IDataEventSink&lt;T&gt;</c> projections, which are untouched.
/// </remarks>
public interface IV4RecordBroadcaster<in TModel> where TModel : class
{
    /// <summary>Broadcast newly created records as <c>create</c> events on their category group.</summary>
    Task BroadcastCreatedAsync(IReadOnlyList<TModel> items, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Broadcast materially-changed records as <c>update</c> events on their category group.</summary>
    Task BroadcastUpdatedAsync(IReadOnlyList<TModel> items, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Broadcast deleted record ids as <c>delete</c> events on their category group.</summary>
    Task BroadcastDeletedAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default) => Task.CompletedTask;
}

using Nocturne.API.Authorization;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Realtime;

/// <summary>
/// Broadcasts a state span written through <c>StateSpansController</c> as a native record, in the
/// shape <see cref="SignalRV4RecordBroadcaster{TModel}"/> sends. The v1 and v3 treatment feeds serve
/// an override or a temporary target written there, and a classic socket client refetches treatments
/// only when a broadcast names them (see <c>legacy-delta.ts</c> in the bridge).
/// </summary>
/// <remarks>
/// A span rides the V4 category whose governing scope is the span category's read scope
/// (<see cref="StateSpanReadScopeGuard"/>), for the reason <see cref="V4BroadcastMap"/> gives. A span
/// category with no such scope broadcasts nothing. Spans written by a decomposed upload or a
/// connector do not come through here: the upload broadcasts its own record.
/// </remarks>
internal static class StateSpanBroadcast
{
    public const string RecordType = "stateSpan";

    private static readonly IReadOnlyDictionary<string, string> CategoryOfReadScope =
        RealtimeCategories.V4.ToDictionary(category => RealtimeCategories.GoverningScopes[category]);

    /// <summary>The broadcast category of a span category, or null when none reads it.</summary>
    public static string? CategoryOf(StateSpanCategory category) =>
        CategoryOfReadScope.GetValueOrDefault(StateSpanReadScopeGuard.RequiredReadScope(category));

    public static Task StateSpanCreatedAsync(this ISignalRBroadcastService broadcast, StateSpan span) =>
        CategoryOf(span.Category) is { } category
            ? broadcast.BroadcastStorageCreateAsync(category, new { colName = category, recordType = RecordType, doc = span })
            : Task.CompletedTask;

    /// <summary>
    /// Broadcasts <paramref name="span"/> as updated, and as deleted from the broadcast category
    /// <paramref name="before"/> rode when the update moved it to another.
    /// </summary>
    public static async Task StateSpanUpdatedAsync(this ISignalRBroadcastService broadcast, StateSpan before, StateSpan span)
    {
        if (CategoryOf(before.Category) != CategoryOf(span.Category))
            await broadcast.StateSpanDeletedAsync(before);

        if (CategoryOf(span.Category) is { } category)
            await broadcast.BroadcastStorageUpdateAsync(category, new { colName = category, recordType = RecordType, doc = span });
    }

    public static Task StateSpanDeletedAsync(this ISignalRBroadcastService broadcast, StateSpan span) =>
        CategoryOf(span.Category) is { } category
            ? broadcast.BroadcastStorageDeleteAsync(category, new { colName = category, recordType = RecordType, id = span.Id })
            : Task.CompletedTask;
}

using Nocturne.Core.Models.Queries;
using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.V4.Repositories;

/// <summary>
/// Reads the stored legacy device statuses as <see cref="DeviceStatusGroup"/>s, one per decomposed
/// status, whichever of the APS, pump, uploader and extras rows it kept.
/// </summary>
/// <remarks>
/// A status is placed by its anchor, the first of APS, pump, uploader and extras it has: its
/// timestamp and device are the ones the window and device filters test. Rows sharing a correlation
/// id are one status; a row without one is a status of its own. Every read here agrees on that
/// anchor set, so a page, the count and the history each see the same statuses.
/// </remarks>
public interface IDeviceStatusGroupRepository
{
    /// <summary>
    /// A page of statuses whose anchor falls in the inclusive window and, when given, has
    /// <paramref name="device"/>, newest first or, when <paramref name="descending"/> is
    /// <c>false</c>, oldest first with <paramref name="offset"/> counted from the oldest.
    /// </summary>
    Task<IReadOnlyList<DeviceStatusGroup>> GetAsync(
        DateTime? from, DateTime? to, string? device, int limit, int offset, bool descending, CancellationToken ct = default);

    /// <summary>The number of statuses <see cref="GetAsync"/> pages through.</summary>
    Task<long> CountAsync(DateTime? from, DateTime? to, string? device, CancellationToken ct = default);

    /// <summary>
    /// Statuses with a row modified after <paramref name="cursorMills"/>, oldest
    /// <see cref="DeviceStatusGroup.ModifiedAt"/> first, paged as
    /// <see cref="ILegacyKeyedRepository{TRecord}.GetModifiedSinceAsync"/> pages one table. A status is
    /// deleted only when every row of it is.
    /// </summary>
    Task<IReadOnlyList<HistoryRecord<DeviceStatusGroup>>> GetModifiedSinceAsync(
        long cursorMills, int limit, CancellationToken ct = default);
}

using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Profiles.Resolvers;

/// <summary>
/// The active profile at a time, read from the <see cref="IProfileActivationResolver"/>.
/// </summary>
internal sealed class ActiveProfileResolver : IActiveProfileResolver
{
    private readonly IProfileActivationResolver _activations;
    private readonly IProfileActivationRepository _repository;
    private readonly ProfileStoreTimelineSource _stores;

    public ActiveProfileResolver(
        IProfileActivationResolver activations,
        IProfileActivationRepository repository,
        ProfileStoreTimelineSource stores)
    {
        _activations = activations;
        _repository = repository;
        _stores = stores;
    }

    public async Task<string?> GetActiveProfileNameAsync(long timeMills, CancellationToken ct = default) =>
        (await _activations.GetAtAsync(timeMills, ct))?.ProfileName;

    public async Task<string> GetProfileNameAsync(long timeMills, CancellationToken ct = default) =>
        (await _stores.GetAsync(ct)).StoreFor(await GetActiveProfileNameAsync(timeMills, ct), timeMills);

    public async Task<string> GetDefaultProfileNameAsync(long timeMills, CancellationToken ct = default) =>
        (await _stores.GetAsync(ct)).DefaultAt(timeMills);

    public async Task<CircadianAdjustment?> GetCircadianAdjustmentAsync(long timeMills, CancellationToken ct = default) =>
        (await _activations.GetAtAsync(timeMills, ct))?.Scaling;

    public async Task<ProfileActivationSnapshot?> GetActiveSchedulesAsync(long timeMills, CancellationToken ct = default) =>
        (await _activations.GetAtAsync(timeMills, ct))?.Schedules;

    public async Task<IReadOnlyList<ProfileSpan>> GetProfileSpansForRangeAsync(
        long fromMs, long toMs, CancellationToken ct = default)
    {
        if (toMs <= fromMs)
            toMs = fromMs + 1;

        var stores = await _stores.GetAsync(ct);
        var spans = new List<ProfileSpan>();
        var cursor = fromMs;
        foreach (var segment in await _activations.GetRangeAsync(fromMs, toMs, ct))
        {
            if (segment.StartMills > cursor)
                AddCut(spans, stores, cursor, segment.StartMills, null);
            var end = Math.Min(segment.EndMills, toMs);
            AddCut(spans, stores, Math.Max(segment.StartMills, cursor), end, segment.Active);
            cursor = Math.Max(cursor, end);
        }

        if (cursor < toMs)
            AddCut(spans, stores, cursor, toMs, null);
        return spans;
    }

    /// <summary>
    /// Adds [<paramref name="fromMs"/>, <paramref name="toMs"/>) under <paramref name="active"/>, cut
    /// wherever the store it reads changes with the profile document in force.
    /// </summary>
    private static void AddCut(
        List<ProfileSpan> spans, ProfileStoreTimeline stores, long fromMs, long toMs, ResolvedProfileActivation? active)
    {
        if (toMs <= fromMs)
            return;

        var start = fromMs;
        var store = stores.StoreFor(active?.ProfileName, fromMs);
        foreach (var change in stores.ChangesWithin(fromMs, toMs))
        {
            var next = stores.StoreFor(active?.ProfileName, change);
            if (string.Equals(next, store, StringComparison.Ordinal))
                continue;
            spans.Add(new ProfileSpan(store, start, change, active?.Scaling, active?.Schedules));
            (start, store) = (change, next);
        }

        spans.Add(new ProfileSpan(store, start, toMs, active?.Scaling, active?.Schedules));
    }

    /// <remarks>
    /// An effective activation uploaded without the insulin falls back to the requested activation
    /// it confirms, which an uploader may have sent it on instead.
    /// </remarks>
    public async Task<TreatmentInsulinContext?> GetActiveInsulinContextAsync(long timeMills, CancellationToken ct = default)
    {
        if ((await _activations.GetAtAsync(timeMills, ct))?.Activation is not { } active)
            return null;

        if (active.Snapshot?.Insulin is { } insulin)
            return insulin;

        return active.RequestedActivationId is { } requestedId
            ? (await _repository.GetByIdAsync(requestedId, ct))?.Snapshot?.Insulin
            : null;
    }
}

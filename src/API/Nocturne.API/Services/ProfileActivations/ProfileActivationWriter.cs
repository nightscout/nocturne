using Nocturne.API.Services.Caching;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.ProfileActivations;

/// <summary>The ends and confirmation links one activation settles in its source family.</summary>
/// <param name="Confirms">The requested activation each effective one confirms, or null for none.</param>
internal sealed record ProfileActivationSettlement(
    IReadOnlyDictionary<Guid, DateTime?> Ends,
    IReadOnlyDictionary<Guid, Guid?> Confirms);

/// <summary>
/// Settles what writing or removing an activation changes in its <see cref="ProfileActivationSource"/>
/// family: the ends that bound each activation's stretch, and which requested activation an effective
/// one confirms.
/// </summary>
/// <remarks>
/// Within one phase of a family, a permanent activation ends at the start of the next permanent one and
/// a temporary one at its requested length or the next start of either kind, whichever is first. A
/// temporary activation leaves a permanent one running, which applies again once it ends
/// (<see cref="ProfileActivationSelection"/>). A requested activation also ends where the first
/// effective activation that confirms it starts.
///
/// An effective activation confirms the family's newest requested activation at or before it, if that
/// runs the same profile, percentage and timeshift. A temporary request whose length ran out before the
/// effective one started is passed over for the one before it, as AAPS records an effective switch
/// back to the permanent profile when a temporary one runs out; any other request in between leaves
/// it unconfirmed. Several may confirm one request.
///
/// These ends are what the newest-start-wins selection reads, so removing an activation settles again
/// every neighbour it ended or that confirmed it (<see cref="ResettleAfterRemovalAsync"/>).
/// </remarks>
public sealed class ProfileActivationWriter
{
    /// <summary>How many neighbours of a written activation are read on either side of it.</summary>
    private const int Neighbourhood = 200;

    private readonly IProfileActivationRepository _repository;
    private readonly ITenantCache _cache;

    public ProfileActivationWriter(IProfileActivationRepository repository, ITenantCache cache)
    {
        _repository = repository;
        _cache = cache;
    }

    /// <summary>
    /// Writes <paramref name="activation"/> through <paramref name="store"/> with the end and confirmation
    /// its family gives it, then settles its neighbours. An identical re-send therefore writes nothing.
    /// </summary>
    /// <param name="activation">The activation to write, carrying the end it was uploaded with: its own length, or none.</param>
    /// <param name="stored">The live row <paramref name="activation"/> replaces, if any.</param>
    /// <param name="store">The write; null when it was refused.</param>
    public async Task<ProfileActivation?> WriteAsync(
        ProfileActivation activation,
        ProfileActivation? stored,
        Func<ProfileActivation, Task<ProfileActivation?>> store,
        CancellationToken ct)
    {
        activation.Id = stored?.Id ?? (activation.Id == Guid.Empty ? Guid.CreateVersion7() : activation.Id);
        var settlement = await PlanAsync(activation, ct);
        activation.EndTimestamp = settlement.Ends[activation.Id];
        activation.RequestedActivationId = settlement.Confirms.GetValueOrDefault(activation.Id);

        var written = await store(activation);
        if (written is null)
            return null;

        await ApplyAsync(settlement, except: written.Id, ct);
        if (stored is not null && MovedFrom(stored, written))
            await ResettleNeighboursOfAsync([stored], ct);
        await InvalidateAsync(ct);
        return written;
    }

    /// <summary>Settles again the live neighbours of the removed activations, then clears the cache.</summary>
    public async Task ResettleAfterRemovalAsync(IReadOnlyCollection<Guid> removedIds, CancellationToken ct)
    {
        if (removedIds.Count > 0)
            await ResettleNeighboursOfAsync(await _repository.GetIncludingDeletedAsync(removedIds, ct), ct);
        await InvalidateAsync(ct);
    }

    public Task InvalidateAsync(CancellationToken ct) =>
        _cache.RemoveFamilyAsync(TenantCacheFamily.ProfileActivations, ct);

    /// <summary>
    /// Each live activation of the families of <paramref name="removed"/> that one of them ended or
    /// confirmed, settled oldest first as if written again with only the end it brings with it.
    /// </summary>
    private async Task ResettleNeighboursOfAsync(IReadOnlyList<ProfileActivation> removed, CancellationToken ct)
    {
        if (removed.Count == 0)
            return;

        var removedIds = removed.Select(r => r.Id).ToHashSet();
        var settledBy = await _repository.GetSettledByAsync(
            removed.Select(r => r.StartTimestamp).Distinct().ToList(), removedIds, ct);
        var affected = settledBy
            .Where(a => !removedIds.Contains(a.Id)
                && removed.Any(r => ProfileActivationSource.KeyOf(r) == ProfileActivationSource.KeyOf(a)
                    && (a.EndTimestamp == r.StartTimestamp || a.RequestedActivationId == r.Id)))
            .ToList();

        foreach (var activation in affected)
        {
            activation.EndTimestamp = StoredNaturalEnd(activation);
            await ApplyAsync(await PlanAsync(activation, ct), except: null, ct);
        }
    }

    private async Task<ProfileActivationSettlement> PlanAsync(ProfileActivation activation, CancellationToken ct)
    {
        var start = activation.StartTimestamp;
        var running = await _repository.GetOverlappingAsync(start, start, Neighbourhood, ct);
        var later = await _repository.GetStartingAfterAsync(start, Neighbourhood, ct);
        var requested = activation.Phase == ProfileActivationPhase.Effective
            ? await _repository.GetRequestedAtOrBeforeAsync(start, Neighbourhood, ct)
            : [];
        return Settle(activation, running, later, requested);
    }

    private async Task ApplyAsync(ProfileActivationSettlement settlement, Guid? except, CancellationToken ct)
    {
        await _repository.SetEndsAsync(
            settlement.Ends.Where(e => e.Key != except).ToDictionary(e => e.Key, e => e.Value), ct);
        foreach (var group in settlement.Confirms.Where(c => c.Key != except).GroupBy(c => c.Value, c => c.Key))
            await _repository.SetRequestedActivationAsync(group.ToList(), group.Key, ct);
    }

    /// <param name="written">The activation being settled, carrying only the end it brings with it.</param>
    /// <param name="running">Activations started at or before the written one and running at its start.</param>
    /// <param name="later">Activations started after the written one, oldest first.</param>
    /// <param name="requested">Requested activations started at or before the written one, newest first.</param>
    internal static ProfileActivationSettlement Settle(
        ProfileActivation written,
        IEnumerable<ProfileActivation> running,
        IEnumerable<ProfileActivation> later,
        IEnumerable<ProfileActivation> requested)
    {
        var family = ProfileActivationSource.KeyOf(written);
        bool InFamily(ProfileActivation a) => a.Id != written.Id && ProfileActivationSource.KeyOf(a) == family;

        var start = written.StartTimestamp;
        var after = later.Where(InFamily).OrderBy(a => a.StartTimestamp).ThenBy(a => a.Id).ToList();
        var ends = new Dictionary<Guid, DateTime?>();
        var confirms = new Dictionary<Guid, Guid?>();

        foreach (var previous in running.Where(InFamily))
        {
            if (previous.Phase == written.Phase
                && previous.StartTimestamp < start
                && (!written.IsTemporary || previous.IsTemporary))
                ends[previous.Id] = start;
        }

        var nextOfPhase = after
            .Where(a => a.Phase == written.Phase)
            .FirstOrDefault(a => written.IsTemporary || !a.IsTemporary);
        var end = Earliest(NaturalEnd(written), nextOfPhase?.StartTimestamp);

        if (written.Phase == ProfileActivationPhase.Effective)
        {
            var confirmed = ConfirmedBy(written, requested.Where(InFamily));
            confirms[written.Id] = confirmed?.Id;
            if (confirmed is not null && (confirmed.EndTimestamp is not { } confirmedEnd || confirmedEnd > start))
                ends[confirmed.Id] = start;
        }
        else
        {
            // Each effective activation up to the family's next request has this one as its newest.
            var nextRequested = after.FirstOrDefault(a => a.Phase == ProfileActivationPhase.Requested)?.StartTimestamp;
            DateTime? firstConfirmation = null;
            foreach (var effective in after.Where(a => a.Phase == ProfileActivationPhase.Effective
                && (nextRequested is null || a.StartTimestamp < nextRequested)))
            {
                if (Runs(effective, written))
                {
                    confirms[effective.Id] = written.Id;
                    firstConfirmation ??= effective.StartTimestamp;
                }
                else if (!RanOutBefore(written, effective.StartTimestamp))
                    confirms[effective.Id] = null;
            }
            if (firstConfirmation is not null)
                end = Earliest(end, firstConfirmation);
        }

        ends[written.Id] = end;
        return new ProfileActivationSettlement(ends, confirms);
    }

    /// <summary>
    /// The request <paramref name="effective"/> confirms among its family's, newest first: the newest,
    /// passing over temporary ones whose length ran out before it started.
    /// </summary>
    private static ProfileActivation? ConfirmedBy(ProfileActivation effective, IEnumerable<ProfileActivation> requested)
    {
        foreach (var candidate in requested.Where(r => r.StartTimestamp <= effective.StartTimestamp))
        {
            if (Runs(candidate, effective))
                return candidate;
            if (!RanOutBefore(candidate, effective.StartTimestamp))
                return null;
        }

        return null;
    }

    private static bool RanOutBefore(ProfileActivation request, DateTime at) =>
        request.RequestedDurationMs is > 0 and var duration && request.StartTimestamp.AddMilliseconds(duration) <= at;

    /// <summary>
    /// The end an activation being written brings with it: the end an effective one was reported with
    /// (AAPS's for a temporary switch, a connector's next reading), or a temporary requested one's start
    /// plus its length. A permanent requested one has none.
    /// </summary>
    private static DateTime? NaturalEnd(ProfileActivation activation) =>
        activation.Phase == ProfileActivationPhase.Effective
            ? activation.EndTimestamp
            : activation.RequestedDurationMs is > 0 and var duration ? activation.StartTimestamp.AddMilliseconds(duration)
            : null;

    /// <summary>
    /// <see cref="NaturalEnd"/> of a stored activation, whose end its family has since settled: an
    /// effective one's is read back from its residue (<see cref="ProfileActivationLegacyFields.ReportedEnd"/>),
    /// so a connector's, which keeps none, runs until its family's next one.
    /// </summary>
    private static DateTime? StoredNaturalEnd(ProfileActivation activation) =>
        activation.Phase == ProfileActivationPhase.Effective
            ? ProfileActivationLegacyFields.ReportedEnd(activation)
            : NaturalEnd(activation);

    private static bool MovedFrom(ProfileActivation stored, ProfileActivation written) =>
        stored.StartTimestamp != written.StartTimestamp
        || stored.Phase != written.Phase
        || ProfileActivationSource.KeyOf(stored) != ProfileActivationSource.KeyOf(written);

    private static bool Runs(ProfileActivation a, ProfileActivation b) =>
        a.BaseProfileName == b.BaseProfileName && a.Percentage == b.Percentage && a.TimeshiftMs == b.TimeshiftMs;

    private static DateTime? Earliest(DateTime? a, DateTime? b) =>
        a is null ? b : b is null ? a : a < b ? a : b;
}

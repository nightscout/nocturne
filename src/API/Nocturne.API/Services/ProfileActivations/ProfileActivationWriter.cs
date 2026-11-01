using Nocturne.API.Services.Caching;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.ProfileActivations;

/// <summary>An activation to write, and the live row it replaces, if any.</summary>
public sealed record ProfileActivationWrite(ProfileActivation Activation, ProfileActivation? Stored);

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
/// Within one phase of a family, an unbounded activation ends at the start of the next unbounded one and
/// a bounded one (<see cref="ProfileActivation.IsBounded"/>: temporary, or ended by a person) at its own
/// end or the next start of either kind, whichever is first. A bounded activation leaves an unbounded
/// one running, which applies again once it ends (<see cref="ProfileActivationSelection"/>). A requested activation also ends where the first
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
    private readonly ITempBasalRateReresolver _tempBasalRates;

    public ProfileActivationWriter(
        IProfileActivationRepository repository, ITenantCache cache, ITempBasalRateReresolver tempBasalRates)
    {
        _repository = repository;
        _cache = cache;
        _tempBasalRates = tempBasalRates;
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
        CancellationToken ct) =>
        (await WriteBatchAsync([new ProfileActivationWrite(activation, stored)], async batch => [await store(batch[0])], ct))[0];

    /// <summary>
    /// Writes <paramref name="writes"/> through <paramref name="store"/> in one call, each with the end and
    /// confirmation its family gives it once the writes before it are written, as <see cref="WriteAsync"/>
    /// one at a time in that order would, then settles the neighbours they change in one save. The
    /// neighbourhood is read once, around every start the writes touch.
    /// </summary>
    /// <param name="store">The write, returning per activation the row written, or null when it was refused.</param>
    /// <remarks>
    /// A write the store refuses leaves its neighbours as they were only when every write is refused, so
    /// a caller withholds the writes it knows the store refuses.
    /// </remarks>
    public async Task<IReadOnlyList<ProfileActivation?>> WriteBatchAsync(
        IReadOnlyList<ProfileActivationWrite> writes,
        Func<IReadOnlyList<ProfileActivation>, Task<IReadOnlyList<ProfileActivation?>>> store,
        CancellationToken ct)
    {
        if (writes.Count == 0)
            return [];

        foreach (var (activation, stored) in writes)
            activation.Id = stored?.Id ?? (activation.Id == Guid.Empty ? Guid.CreateVersion7() : activation.Id);

        var family = await LoadAsync(
            writes.SelectMany(w => w.Stored is { } stored
                ? new[] { w.Activation.StartTimestamp, stored.StartTimestamp }
                : [w.Activation.StartTimestamp]),
            ct);

        var moved = new List<ProfileActivation>();
        foreach (var (activation, stored) in writes)
        {
            var settlement = family.Plan(activation);
            activation.EndTimestamp = settlement.Ends[activation.Id];
            activation.RequestedActivationId = settlement.Confirms.GetValueOrDefault(activation.Id);
            family.Put(activation);
            family.Apply(settlement, except: activation.Id);
            if (stored is not null && MovedFrom(stored, activation))
            {
                family.Resettle([stored], family.Members);
                moved.Add(stored);
            }
        }

        var written = await store([.. writes.Select(w => w.Activation)]);
        if (written.All(w => w is null))
            return written;

        await family.SaveAsync(_repository, except: writes.Select(w => w.Activation.Id).ToHashSet(), ct);
        await InvalidateAsync(ct);
        await _tempBasalRates.AfterActivationsChangedAsync(
            [.. written.OfType<ProfileActivation>().Select(Span), .. moved.Select(Span)], ct);
        return written;
    }

    /// <summary>
    /// Ends the requested <paramref name="activation"/> at <paramref name="at"/>, or at its own length if
    /// that is sooner, and writes it through <paramref name="store"/> as <see cref="WriteAsync"/> does.
    /// Ended, it is bounded, so the activation it superseded runs again and applies once it ends.
    /// </summary>
    public async Task<ProfileActivation?> EndAsync(
        ProfileActivation activation,
        DateTime at,
        string? endedBy,
        Func<ProfileActivation, Task<ProfileActivation?>> store,
        CancellationToken ct)
    {
        activation.EndedAt = at;
        activation.EndedBy = endedBy;
        var written = await WriteAsync(activation, activation, store, ct);
        if (written is not null)
        {
            await ResettleNeighboursOfAsync([written], ct);
            await InvalidateAsync(ct);
        }
        return written;
    }

    /// <summary>Settles again the live neighbours of the removed activations, then clears the cache.</summary>
    public async Task ResettleAfterRemovalAsync(IReadOnlyCollection<Guid> removedIds, CancellationToken ct)
    {
        IReadOnlyList<ProfileActivation> removed =
            removedIds.Count > 0 ? await _repository.GetIncludingDeletedAsync(removedIds, ct) : [];
        await ResettleNeighboursOfAsync(removed, ct);
        await InvalidateAsync(ct);
        await _tempBasalRates.AfterActivationsChangedAsync(removed.Select(Span).ToList(), ct);
    }

    private static (string ProfileName, DateTime Start, DateTime? End) Span(ProfileActivation activation) =>
        (activation.BaseProfileName, activation.StartTimestamp, activation.EndTimestamp);

    public Task InvalidateAsync(CancellationToken ct) =>
        _cache.RemoveFamilyAsync(TenantCacheFamily.ProfileActivations, ct);

    /// <summary>
    /// Clears the cache and re-resolves every schedule-derived temp basal rate, after activations
    /// were removed in bulk without being read. <paramref name="profileNames"/> are the profiles whose
    /// schedules the same removal deleted.
    /// </summary>
    public async Task AfterBulkRemovalAsync(IReadOnlyCollection<string> profileNames, CancellationToken ct)
    {
        await InvalidateAsync(ct);
        await _tempBasalRates.AfterProfilesChangedAsync(profileNames, ct);
    }

    /// <summary>
    /// Each live activation of the families of <paramref name="removed"/> that one of them ended or
    /// confirmed, settled oldest first as if written again with only the end it brings with it.
    /// </summary>
    private async Task ResettleNeighboursOfAsync(IReadOnlyList<ProfileActivation> removed, CancellationToken ct)
    {
        if (removed.Count == 0)
            return;

        var settledBy = await _repository.GetSettledByAsync(
            removed.Select(r => r.StartTimestamp).Distinct().ToList(), removed.Select(r => r.Id).ToHashSet(), ct);
        if (settledBy.Count == 0)
            return;

        var family = await LoadAsync(settledBy.Select(a => a.StartTimestamp), ct);
        family.Resettle(removed, [.. settledBy.Select(family.Track)]);
        await family.SaveAsync(_repository, except: new HashSet<Guid>(), ct);
    }

    /// <summary>
    /// The live activations every settlement around <paramref name="starts"/> reads: those running in
    /// their span, the <see cref="Neighbourhood"/> starting after it, and the <see cref="Neighbourhood"/>
    /// newest requested ones at or before it. One ending exactly at the span's first start is read too,
    /// for <see cref="Family.Resettle"/>.
    /// </summary>
    private async Task<Family> LoadAsync(IEnumerable<DateTime> starts, CancellationToken ct)
    {
        var span = starts.ToList();
        var (from, to) = (span.Min(), span.Max());
        var running = await _repository.GetOverlappingAsync(from.AddTicks(-10), to, int.MaxValue, ct);
        var later = await _repository.GetStartingAfterAsync(to, Neighbourhood, ct);
        var requested = await _repository.GetRequestedAtOrBeforeAsync(from, Neighbourhood, ct);
        return new Family([.. running, .. later, .. requested]);
    }

    /// <summary>
    /// The activations a set of settlements reads and changes, held in memory so each settlement sees the
    /// ones before it, then saved as the ends and confirmations that changed.
    /// </summary>
    private sealed class Family
    {
        private readonly Dictionary<Guid, ProfileActivation> _members = [];
        private readonly Dictionary<Guid, (DateTime? End, Guid? Requested)> _loaded = [];

        public Family(IEnumerable<ProfileActivation> loaded)
        {
            foreach (var activation in loaded)
                Track(activation);
        }

        public IReadOnlyList<ProfileActivation> Members => [.. _members.Values];

        /// <summary>The member with <paramref name="activation"/>'s id, which joins as read if it is not one.</summary>
        public ProfileActivation Track(ProfileActivation activation)
        {
            if (_members.TryGetValue(activation.Id, out var member))
                return member;
            _members[activation.Id] = activation;
            _loaded[activation.Id] = (activation.EndTimestamp, activation.RequestedActivationId);
            return activation;
        }

        public void Put(ProfileActivation activation) => _members[activation.Id] = activation;

        /// <summary>
        /// What one activation's settlement reads, from the members: the newest <see cref="Neighbourhood"/>
        /// running at its start, the oldest starting after it, and for an effective one the newest requested
        /// at or before it.
        /// </summary>
        public ProfileActivationSettlement Plan(ProfileActivation activation)
        {
            var start = activation.StartTimestamp;
            var running = _members.Values
                .Where(a => a.StartTimestamp <= start && (a.EndTimestamp == null || a.EndTimestamp > start))
                .OrderByDescending(a => a.StartTimestamp).ThenByDescending(a => a.Id)
                .Take(Neighbourhood)
                .Reverse();
            var later = _members.Values
                .Where(a => a.StartTimestamp > start)
                .OrderBy(a => a.StartTimestamp).ThenBy(a => a.Id)
                .Take(Neighbourhood);
            var requested = activation.Phase == ProfileActivationPhase.Effective
                ? _members.Values
                    .Where(a => a.Phase == ProfileActivationPhase.Requested && a.StartTimestamp <= start)
                    .OrderByDescending(a => a.StartTimestamp).ThenByDescending(a => a.Id)
                    .Take(Neighbourhood)
                : [];
            return Settle(activation, running.ToList(), later.ToList(), requested.ToList());
        }

        public void Apply(ProfileActivationSettlement settlement, Guid? except)
        {
            foreach (var (id, end) in settlement.Ends)
            {
                if (id != except && _members.TryGetValue(id, out var member))
                    member.EndTimestamp = end;
            }

            foreach (var (id, requested) in settlement.Confirms)
            {
                if (id != except && _members.TryGetValue(id, out var member))
                    member.RequestedActivationId = requested;
            }
        }

        /// <summary>
        /// Each of <paramref name="candidates"/> in the family of one of <paramref name="removed"/> that one
        /// of them ended or confirmed, settled oldest first as if written again with only the end it brings.
        /// </summary>
        public void Resettle(IReadOnlyList<ProfileActivation> removed, IReadOnlyList<ProfileActivation> candidates)
        {
            var removedIds = removed.Select(r => r.Id).ToHashSet();
            var affected = candidates
                .Where(a => !removedIds.Contains(a.Id)
                    && removed.Any(r => ProfileActivationSource.KeyOf(r) == ProfileActivationSource.KeyOf(a)
                        && (a.EndTimestamp == r.StartTimestamp || a.RequestedActivationId == r.Id)))
                .OrderBy(a => a.StartTimestamp).ThenBy(a => a.Id)
                .ToList();

            foreach (var activation in affected)
            {
                activation.EndTimestamp = StoredNaturalEnd(activation);
                Apply(Plan(activation), except: null);
            }
        }

        /// <summary>Saves the ends and confirmations that changed on the members read, but those in <paramref name="except"/>.</summary>
        public Task SaveAsync(IProfileActivationRepository repository, IReadOnlySet<Guid> except, CancellationToken ct)
        {
            var changed = _loaded
                .Where(l => !except.Contains(l.Key))
                .Select(l => (Member: _members[l.Key], Loaded: l.Value))
                .ToList();
            var ends = changed.Where(c => c.Member.EndTimestamp != c.Loaded.End)
                .ToDictionary(c => c.Member.Id, c => c.Member.EndTimestamp);
            var confirms = changed.Where(c => c.Member.RequestedActivationId != c.Loaded.Requested)
                .ToDictionary(c => c.Member.Id, c => c.Member.RequestedActivationId);
            return ends.Count == 0 && confirms.Count == 0 ? Task.CompletedTask : repository.SettleAsync(ends, confirms, ct);
        }
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
                && (!written.IsBounded || previous.IsBounded))
                ends[previous.Id] = start;
        }

        var nextOfPhase = after
            .Where(a => a.Phase == written.Phase)
            .FirstOrDefault(a => written.IsBounded || !a.IsBounded);
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
    /// (AAPS's for a temporary switch, a connector's next reading), or a requested one's start plus its
    /// length or the moment a person ended it, whichever is first. A permanent requested one that nobody
    /// ended has none.
    /// </summary>
    private static DateTime? NaturalEnd(ProfileActivation activation) =>
        activation.Phase == ProfileActivationPhase.Effective
            ? activation.EndTimestamp
            : Earliest(
                activation.RequestedDurationMs is > 0 and var duration ? activation.StartTimestamp.AddMilliseconds(duration) : null,
                activation.EndedAt);

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

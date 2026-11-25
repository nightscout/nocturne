using Microsoft.EntityFrameworkCore;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Mappers;

namespace Nocturne.Infrastructure.Data.Repositories;

/// <summary>
/// The rule that ends an open span of an exclusive category at the next stored span it conflicts with.
/// Every write that moves supersession applies it through <see cref="Extensions.StateSpanSupersessionExtensions"/>:
/// an upload, a fold, the delete and the restore of a superseder, and a replace.
/// </summary>
internal static class StateSpanSuccession
{
    /// <summary>
    /// Categories where only one span can be active at a time.
    /// When a new span is inserted in one of these categories, any existing open spans are closed.
    /// </summary>
    internal static readonly HashSet<string> ExclusiveCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(StateSpanCategory.Override),
        nameof(StateSpanCategory.TemporaryTarget),
        nameof(StateSpanCategory.PumpMode),
        nameof(StateSpanCategory.Motion),
    };

    /// <summary>Stored spans read per open span, the nearest first, as its successor candidates.</summary>
    internal const int Candidates = 10;

    /// <summary>Spans whose neighbours one statement reads.</summary>
    internal const int LookupsPerStatement = 200;

    /// <summary>
    /// PumpMode mixes independent dimensions (Automatic/Manual loop mode vs Suspended delivery) which
    /// can legitimately overlap, so only the same state is mutually exclusive there. Other exclusive
    /// categories conflict whatever their state.
    /// </summary>
    internal static bool SameStateOnly(string category) =>
        string.Equals(category, nameof(StateSpanCategory.PumpMode), StringComparison.OrdinalIgnoreCase);

    /// <summary>A span can follow only spans of its category, and in PumpMode only of its state.</summary>
    internal static (string Category, string? State, DateTime Start) Key(string category, string? state, DateTime start) =>
        (category, SameStateOnly(category) ? state : null, start);

    /// <summary>
    /// Whether <paramref name="later"/> is a live span that ends <paramref name="open"/>, whose metadata
    /// is <paramref name="metadata"/>.
    /// </summary>
    internal static bool Follows(
        StateSpanEntity open, IDictionary<string, object>? metadata, StateSpanEntity later) =>
        later.DeletedAt == null
        && string.Equals(later.Category, open.Category, StringComparison.Ordinal)
        && later.StartTimestamp > open.StartTimestamp
        && (!SameStateOnly(open.Category) || string.Equals(later.State, open.State, StringComparison.Ordinal))
        && Supersedes(open, metadata, later);

    /// <summary>
    /// Whether <paramref name="entity"/> ends <paramref name="other"/> in an exclusive category.
    /// Loop uploads one override as a treatment and as devicestatus snapshots, so a treatment span
    /// and a devicestatus span of the same override do not end each other. Two spans of one kind
    /// do, even of the same preset and whatever their sources.
    /// </summary>
    internal static bool Supersedes(
        StateSpanEntity entity, IDictionary<string, object>? metadata, StateSpanEntity other) =>
        !string.Equals(entity.Category, nameof(StateSpanCategory.Override), StringComparison.OrdinalIgnoreCase)
        || !metadata.IsSameOverrideAs(MapperHelpers.DeserializeJson<Dictionary<string, object>>(other.MetadataJson));

    /// <summary>
    /// Per key, the first <see cref="Candidates"/> stored live spans a span of that key can follow,
    /// oldest first: one statement per <see cref="LookupsPerStatement"/> keys.
    /// </summary>
    internal static async Task<Dictionary<(string Category, string? State, DateTime Start), List<StateSpanEntity>>>
        LoadCandidatesAsync(
            NocturneDbContext db,
            IReadOnlyCollection<(string Category, string? State, DateTime Start)> keys,
            CancellationToken cancellationToken)
    {
        var later = keys.ToDictionary(k => k, _ => new List<StateSpanEntity>());
        foreach (var chunk in keys.Chunk(LookupsPerStatement))
        {
            IQueryable<CandidateRow>? query = null;
            for (var i = 0; i < chunk.Length; i++)
            {
                var lookup = i;
                var rows = After(db, chunk[i])
                    .Take(Candidates)
                    .Select(s => new CandidateRow { Lookup = lookup, Span = s });
                query = query is null ? rows : query.Concat(rows);
            }

            foreach (var group in (await query!.ToListAsync(cancellationToken)).GroupBy(r => r.Lookup))
                later[chunk[group.Key]] = [.. group.Select(r => r.Span).OrderBy(s => s.StartTimestamp)];
        }

        return later;
    }

    /// <summary>
    /// The first stored span that <see cref="Follows"/> <paramref name="open"/> and is
    /// <paramref name="eligible"/>. Reads past <paramref name="candidates"/>, the ones
    /// <see cref="LoadCandidatesAsync"/> read for its key, only when every one was passed over and more may
    /// lie beyond them.
    /// </summary>
    internal static async Task<StateSpanEntity?> FirstStoredAsync(
        NocturneDbContext db,
        StateSpanEntity open,
        IDictionary<string, object>? metadata,
        List<StateSpanEntity> candidates,
        Func<StateSpanEntity, bool> eligible,
        CancellationToken cancellationToken)
    {
        var found = candidates.Where(eligible).FirstOrDefault(s => Follows(open, metadata, s));
        if (found is not null || candidates.Count < Candidates)
            return found;

        var further = After(db, Key(open.Category, open.State, candidates[^1].StartTimestamp));
        await foreach (var later in further.AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            if (eligible(later) && Follows(open, metadata, later))
                return later;
        }

        return null;
    }

    private static IQueryable<StateSpanEntity> After(
        NocturneDbContext db, (string Category, string? State, DateTime Start) key)
    {
        var (category, state, start) = key;
        var spans = db.StateSpans.AsNoTracking()
            .Where(s => s.Category == category && s.StartTimestamp > start);
        if (state is not null)
            spans = spans.Where(s => s.State == state);
        return spans.OrderBy(s => s.StartTimestamp);
    }

    /// <summary>Set by member initialisation, which EF can translate on both sides of a union.</summary>
    private sealed class CandidateRow
    {
        public int Lookup { get; init; }
        public StateSpanEntity Span { get; init; } = null!;
    }
}

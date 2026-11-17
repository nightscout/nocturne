using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Profiles.Resolvers;

/// <summary>
/// The stores the profile store holds at each time, and which of them is the default then, as
/// Nightscout reads them off the profile document in force (<c>profileFromTime</c>,
/// <c>activeProfileToTime</c> in lib/profilefunctions.js): a document's stores replace the previous
/// document's, and a profile switch naming a store the document does not hold reads its default.
/// Before the first document, the newest one is in force, as Nightscout's <c>profileFromTime</c>
/// falls back to <c>data[0]</c>.
/// </summary>
/// <remarks>
/// The newest document, and the one holding the tenant's <see cref="TherapySettings.IsDefault"/>
/// row, default to that row's store: a user who picks another default changes the newest document's,
/// as Nightscout's in-place PUT of the document does. Any other document defaults to its recorded
/// <see cref="TherapySettings.DocumentDefaultProfile"/> when it holds that store. A row decomposed
/// before that was recorded defaults to the first of these it holds, since a document usually keeps
/// the <c>defaultProfile</c> of the one before it: the IsDefault row's store; the store the document
/// before it defaulted to; a store named <c>Default</c>, which is what a Nightscout document without
/// a <c>defaultProfile</c> reads; else its newest store.
/// A row written on its own through the V4 API adds its store to the document in force and, unless it
/// is that row or the newest, leaves the default as it was.
/// </remarks>
internal sealed class ProfileStoreTimeline
{
    /// <summary>The store read when there is no profile document at all.</summary>
    private const string ImplicitDefault = "Default";

    private readonly Epoch[] _epochs;
    private readonly Dictionary<string, string> _documentDefaults;

    private ProfileStoreTimeline(Epoch[] epochs, Dictionary<string, string> documentDefaults)
    {
        _epochs = epochs;
        _documentDefaults = documentDefaults;
    }

    /// <summary>The time from <paramref name="StartMills"/> on, until the next epoch starts.</summary>
    private sealed record Epoch(long StartMills, IReadOnlySet<string> Stores, string Default);

    public static ProfileStoreTimeline From(IReadOnlyList<ProfileStoreRow> rows)
    {
        var documents = rows
            .GroupBy(r => r.DocumentId, StringComparer.Ordinal)
            .Select(g => (Start: g.Max(r => r.Timestamp), Id: g.Key, Rows: g.ToList()))
            .OrderBy(d => d.Start)
            .ThenBy(d => d.Id, StringComparer.Ordinal)
            .ToList();

        var chosen = rows.Where(r => r.IsDefault).MaxBy(r => r.Timestamp)?.ProfileName;

        var epochs = new Epoch[documents.Count];
        var documentDefaults = new Dictionary<string, string>(documents.Count, StringComparer.Ordinal);
        var stores = new HashSet<string>(StringComparer.Ordinal);
        string? previous = null;
        for (var i = 0; i < documents.Count; i++)
        {
            var document = documents[i];
            var names = document.Rows.Select(r => r.ProfileName).ToHashSet(StringComparer.Ordinal);
            var fromDocument = document.Rows.Any(r => r.FromDocument);

            stores = fromDocument
                ? names
                : new HashSet<string>(stores.Concat(names), StringComparer.Ordinal);

            var recorded = fromDocument
                ? document.Rows.Select(r => r.DocumentDefault).FirstOrDefault(d => d is not null && names.Contains(d))
                : null;
            var defaultStore = chosen is not null && (i == documents.Count - 1 || document.Rows.Any(r => r.IsDefault))
                ? chosen
                : recorded
                    ?? (fromDocument || previous is null
                        ? DocumentDefault(document.Rows, names, previous, chosen)
                        : previous);

            if (!stores.Contains(defaultStore))
                stores = new HashSet<string>(stores.Append(defaultStore), StringComparer.Ordinal);

            epochs[i] = new Epoch(document.Start.ToMills(), stores, defaultStore);
            documentDefaults[document.Id] = defaultStore;
            previous = defaultStore;
        }

        return new ProfileStoreTimeline(epochs, documentDefaults);
    }

    private static string DocumentDefault(
        List<ProfileStoreRow> rows, HashSet<string> names, string? previous, string? chosen)
    {
        if (chosen is not null && names.Contains(chosen))
            return chosen;
        if (previous is not null && names.Contains(previous))
            return previous;
        if (names.Contains(ImplicitDefault))
            return ImplicitDefault;
        return rows
            .OrderByDescending(r => r.Timestamp)
            .ThenBy(r => r.ProfileName, StringComparer.Ordinal)
            .First()
            .ProfileName;
    }

    /// <summary>
    /// The store the document <paramref name="documentId"/> defaults to while it is in force, or
    /// <c>null</c> when no row of the timeline belongs to it.
    /// </summary>
    public string? DocumentDefaultOf(string documentId) => _documentDefaults.GetValueOrDefault(documentId);

    /// <summary>The store read at <paramref name="timeMills"/> when no profile switch names one.</summary>
    public string DefaultAt(long timeMills) => EpochAt(timeMills)?.Default ?? ImplicitDefault;

    /// <summary>
    /// Whether a document uploaded by <paramref name="timeMills"/> holds <paramref name="store"/>: before
    /// the first, none does, so a profile switch from then reads the schedules it was uploaded with,
    /// the only ones from its own time.
    /// </summary>
    public bool Holds(string store, long timeMills) =>
        EpochAt(timeMills, newestBeforeFirst: false)?.Stores.Contains(store) == true;

    /// <summary>
    /// The store read at <paramref name="timeMills"/> for a profile switch naming
    /// <paramref name="requested"/>, or for none when it is null: the store it names when the document
    /// in force holds it, else that document's default. With no document at all, the store it names.
    /// </summary>
    public string StoreFor(string? requested, long timeMills)
    {
        if (requested is null)
            return DefaultAt(timeMills);
        return EpochAt(timeMills) is { } epoch && !epoch.Stores.Contains(requested)
            ? epoch.Default
            : requested;
    }

    /// <summary>The times inside (<paramref name="fromMills"/>, <paramref name="toMills"/>) a document takes over, in order.</summary>
    public IEnumerable<long> ChangesWithin(long fromMills, long toMills) =>
        _epochs.Select(e => e.StartMills).Where(s => s > fromMills && s < toMills).Distinct();

    private Epoch? EpochAt(long timeMills, bool newestBeforeFirst = true)
    {
        int low = 0, high = _epochs.Length - 1, found = -1;
        while (low <= high)
        {
            var mid = low + (high - low) / 2;
            if (_epochs[mid].StartMills <= timeMills)
            {
                found = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        if (found >= 0)
            return _epochs[found];
        return newestBeforeFirst && _epochs.Length > 0 ? _epochs[^1] : null;
    }
}

/// <summary>The tenant's <see cref="ProfileStoreTimeline"/>, read once per scope.</summary>
/// <remarks>Not cached across requests: no cache family is cleared when a profile write changes it.</remarks>
internal sealed class ProfileStoreTimelineSource
{
    private readonly ITherapySettingsRepository _therapySettings;
    private ProfileStoreTimeline? _timeline;

    public ProfileStoreTimelineSource(ITherapySettingsRepository therapySettings)
    {
        _therapySettings = therapySettings;
    }

    public async Task<ProfileStoreTimeline> GetAsync(CancellationToken ct) =>
        _timeline ??= ProfileStoreTimeline.From(await _therapySettings.GetStoreIndexAsync(ct));
}

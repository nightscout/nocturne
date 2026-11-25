using Nocturne.Infrastructure.Data.Extensions;

namespace Nocturne.API.Services.V4;

/// <summary>
/// A batch's uploads sorted by what each one sent with <c>isValid: false</c> does. Nightscout stores
/// such a document already deleted, so none writes anything live. One whose id holds a live record
/// deletes it when the client named that record (<see cref="InvalidUploadRule{TUpload}.NamesStored"/>),
/// and otherwise is a different record that writes nothing. One whose id holds no row is stored
/// deleted, so v3 history serves it, and one whose id a deleted row already holds writes nothing, as a
/// re-send of it. Both deletes are the system's, so a later upload of the id without <c>isValid</c>
/// writes it again (<see cref="SoftDeleteDedupExtensions.WhereBlocksRecreation{TEntity}"/>). The
/// earlier versions of a client's id in the batch take the validity of the last, so each is answered
/// as what is stored; the versions of a synthetic id are different records.
/// </summary>
internal sealed class InvalidUploads<TUpload>
{
    /// <summary>The uploads that write as usual.</summary>
    public List<TUpload> Writing { get; } = [];

    /// <summary>The uploads with <c>isValid: false</c> whose id names a live record they delete.</summary>
    public List<TUpload> Invalidating { get; } = [];

    /// <summary>The uploads with <c>isValid: false</c> stored as rows already deleted, one per id.</summary>
    public List<TUpload> StoredDeleted { get; } = [];

    /// <summary>
    /// Sorts <paramref name="writing"/>, the uploads of <paramref name="batch"/> that would write if
    /// valid, after the versions of each id across the whole batch take the last one's validity.
    /// </summary>
    public static async Task<InvalidUploads<TUpload>> SortAsync(
        IReadOnlyList<TUpload> writing, IReadOnlyList<TUpload> batch, InvalidUploadRule<TUpload> rule,
        CancellationToken ct)
    {
        var versions = batch
            .Where(b => rule.Id(b) is { } id && !rule.IsSynthetic(id))
            .ToLookup(b => rule.Id(b)!, StringComparer.Ordinal);
        foreach (var upload in writing)
        {
            if (rule.Id(upload) is not { } id)
                continue;
            foreach (var earlier in versions[id].Where(v => rule.Index(v) < rule.Index(upload)))
                rule.SetValid(earlier, rule.IsValid(upload));
        }

        var sorted = new InvalidUploads<TUpload>();
        List<TUpload> unstored = [];
        foreach (var upload in writing)
        {
            if (rule.IsValid(upload) != false)
                sorted.Writing.Add(upload);
            else if (rule.Id(upload) is null)
                continue;
            else if (!rule.HoldsLive(upload))
                unstored.Add(upload);
            else if (rule.NamesStored(upload))
                sorted.Invalidating.Add(upload);
        }

        if (unstored.Count > 0)
        {
            var deleted = await rule.DeletedIds([.. unstored.Select(u => rule.Id(u)!).Distinct(StringComparer.Ordinal)], ct);
            sorted.StoredDeleted.AddRange(unstored
                .Where(u => !deleted.Contains(rule.Id(u)!))
                .GroupBy(u => rule.Id(u)!, StringComparer.Ordinal)
                .Select(versionsOfId => versionsOfId.Last()));
        }

        return sorted;
    }
}

/// <summary>What <see cref="InvalidUploads{TUpload}.SortAsync"/> reads of one kind of upload and the rows stored for it.</summary>
/// <param name="Id">The id the upload was sent under, if any.</param>
/// <param name="Index">The upload's position in its batch.</param>
/// <param name="IsValid">The upload's <c>isValid</c>.</param>
/// <param name="SetValid">Sets the upload's <c>isValid</c>.</param>
/// <param name="HoldsLive">Whether a live record holds the upload's id.</param>
/// <param name="NamesStored">Whether the upload's id is its client's name for the live record that holds it.</param>
/// <param name="DeletedIds">The ids among those given that a deleted row holds.</param>
/// <param name="Synthetic">Whether an id was derived from content rather than sent by its client; none by default.</param>
internal sealed record InvalidUploadRule<TUpload>(
    Func<TUpload, string?> Id,
    Func<TUpload, int> Index,
    Func<TUpload, bool?> IsValid,
    Action<TUpload, bool?> SetValid,
    Func<TUpload, bool> HoldsLive,
    Func<TUpload, bool> NamesStored,
    Func<IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlySet<string>>> DeletedIds,
    Func<string, bool>? Synthetic = null)
{
    public bool IsSynthetic(string id) => Synthetic?.Invoke(id) ?? false;
}

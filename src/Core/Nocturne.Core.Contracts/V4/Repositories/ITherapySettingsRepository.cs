using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.Core.Contracts.V4.Repositories;

/// <summary>
/// Repository for <see cref="TherapySettings"/> records representing a versioned snapshot of all
/// therapy parameters (DIA, targets, schedules) for a named therapy profile.
/// </summary>
/// <remarks>
/// <see cref="TherapySettings"/> is a composite record that groups <see cref="BasalSchedule"/>,
/// <see cref="CarbRatioSchedule"/>, <see cref="SensitivitySchedule"/>, and <see cref="TargetRangeSchedule"/>
/// under a single profile name and effective-from timestamp. Used for historical therapy auditing
/// and for seeding new schedule records when a profile is edited.
/// </remarks>
/// <seealso cref="TherapySettings"/>
/// <seealso cref="IBasalScheduleRepository"/>
/// <seealso cref="ISensitivityScheduleRepository"/>
/// <seealso cref="IV4Repository{T}"/>
public interface ITherapySettingsRepository : IProfileScopedRepository<TherapySettings>
{
    /// <summary>Retrieve a page of <see cref="TherapySettings"/> records filtered by time range, device, and source.</summary>
    /// <param name="from">Inclusive start of the time window, or <c>null</c> for no lower bound.</param>
    /// <param name="to">Inclusive end of the time window, or <c>null</c> for no upper bound.</param>
    /// <param name="device">Optional device identifier filter.</param>
    /// <param name="source">Optional data source filter.</param>
    /// <param name="limit">Maximum number of records to return (default 100).</param>
    /// <param name="offset">Number of records to skip for pagination (default 0).</param>
    /// <param name="descending">When <c>true</c>, results are ordered newest-first (default).</param>
    new Task<IEnumerable<TherapySettings>> GetAsync(
        DateTime? from,
        DateTime? to,
        string? device,
        string? source,
        int limit = 100,
        int offset = 0,
        bool descending = true,
        CancellationToken ct = default
    );

    /// <summary>The rows currently flagged <see cref="TherapySettings.IsDefault"/>, newest first.</summary>
    Task<IReadOnlyList<TherapySettings>> GetDefaultsAsync(CancellationToken ct = default);

    /// <summary>Every live row decomposed from a profile document, as the stores it names and when.</summary>
    Task<IReadOnlyList<ProfileStoreRow>> GetStoreIndexAsync(CancellationToken ct = default);

    /// <summary>The newest row decomposed from a profile document.</summary>
    Task<TherapySettings?> GetNewestDocumentRowAsync(CancellationToken ct = default);

    /// <summary>
    /// The ids of the stored profile documents (see <see cref="TherapySettings.DocumentIdOf"/>), newest
    /// first as Nightscout sorts them: by time descending, then by <c>_id</c> descending.
    /// </summary>
    Task<IReadOnlyList<string>> GetDocumentIdsAsync(CancellationToken ct = default);

    /// <summary>The number of stored profile documents <see cref="GetDocumentIdsAsync"/> lists.</summary>
    Task<long> CountDocumentsAsync(CancellationToken ct = default);

    /// <summary>
    /// A page of the stored profile documents, ranked as <see cref="GetDocumentIdsAsync"/> ranks them
    /// or, when <paramref name="descending"/> is <c>false</c>, in the reverse of that order, each as
    /// its live rows, one per named store.
    /// </summary>
    /// <param name="limit">Maximum number of documents to return.</param>
    /// <param name="offset">Number of documents to skip.</param>
    /// <param name="descending">Whether the newest document comes first.</param>
    Task<IReadOnlyList<IReadOnlyList<TherapySettings>>> GetDocumentsAsync(
        int limit, int offset, bool descending, CancellationToken ct = default);

    /// <summary>
    /// The live rows of the profile document <paramref name="documentId"/>, one per named store,
    /// resolved as <see cref="TherapySettings.DocumentIdOf"/> reads them and leaving out profile-switch
    /// snapshots.
    /// </summary>
    /// <param name="documentId">The document's <c>_id</c>.</param>
    Task<IReadOnlyList<TherapySettings>> GetDocumentRowsAsync(string documentId, CancellationToken ct = default);

    /// <summary>
    /// <see cref="GetDocumentRowsAsync(string, CancellationToken)"/> for each of
    /// <paramref name="documentIds"/>, in one query. A document with no live row is absent.
    /// </summary>
    /// <param name="documentIds">The documents' <c>_id</c> values.</param>
    Task<IReadOnlyDictionary<string, IReadOnlyList<TherapySettings>>> GetDocumentRowsAsync(
        IReadOnlyCollection<string> documentIds, CancellationToken ct = default);

    /// <summary>
    /// Makes the row <paramref name="id"/> the tenant's only <see cref="TherapySettings.IsDefault"/> row,
    /// or clears the flag on every row when <paramref name="id"/> is <c>null</c>.
    /// </summary>
    /// <param name="id">The therapy settings row to flag, or <c>null</c> for none.</param>
    Task SetDefaultAsync(Guid? id, CancellationToken ct = default);
}

/// <summary>One store of a stored profile document, as <see cref="ITherapySettingsRepository.GetStoreIndexAsync"/> lists it.</summary>
/// <param name="DocumentId">The document's <c>_id</c>, as <see cref="TherapySettings.DocumentIdOf"/> computes it.</param>
/// <param name="FromDocument">
/// Whether the row came from a legacy profile document, which carries all of its stores, rather than
/// being written on its own through the V4 API.
/// </param>
/// <param name="IsDefault">See <see cref="TherapySettings.IsDefault"/>.</param>
/// <param name="DocumentDefault">See <see cref="TherapySettings.DocumentDefaultProfile"/>.</param>
public sealed record ProfileStoreRow(
    string DocumentId, bool FromDocument, string ProfileName, DateTime Timestamp, bool IsDefault,
    string? DocumentDefault = null);

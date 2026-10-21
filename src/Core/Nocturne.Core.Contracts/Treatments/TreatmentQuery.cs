namespace Nocturne.Core.Contracts.Treatments;

/// <summary>
/// Value object encapsulating query parameters for treatment reads.
/// Used by <see cref="ITreatmentStore"/> and <see cref="ITreatmentCache"/> to filter and paginate results.
/// </summary>
/// <seealso cref="ITreatmentStore"/>
/// <seealso cref="ITreatmentCache"/>
public sealed record TreatmentQuery
{
    /// <summary>
    /// Optional Nightscout-compatible <c>find</c> query string for server-side filtering.
    /// </summary>
    public string? Find { get; init; }

    /// <summary>
    /// Maximum number of treatments to return. Defaults to 10.
    /// </summary>
    public int Count { get; init; } = 10;

    /// <summary>
    /// Number of treatments to skip for pagination. Defaults to 0.
    /// </summary>
    public int Skip { get; init; } = 0;

    /// <summary>
    /// When <c>true</c>, results are returned in ascending chronological order
    /// instead of the default descending order.
    /// </summary>
    public bool ReverseResults { get; init; } = false;

    /// <summary>
    /// The field results are ordered by; <see cref="ReverseResults"/> sets the direction. Only
    /// <see cref="ReadSortField.SrvModified"/> differs from time order.
    /// </summary>
    public ReadSortField SortField { get; init; } = ReadSortField.Time;
}

namespace Nocturne.Core.Contracts.Treatments;

/// <summary>
/// Value object encapsulating query parameters for treatment reads.
/// </summary>
/// <seealso cref="ITreatmentService.QueryTreatmentsAsync"/>
public sealed record TreatmentQuery
{
    /// <summary>
    /// Optional Nightscout-compatible <c>find</c> query string for server-side filtering.
    /// </summary>
    public string? Find { get; init; }

    /// <summary>
    /// Whether a <see cref="Find"/> that names <c>isValid</c> reads the rows stored deleted, as a v1
    /// find does. A v3 search sets it false (<see cref="Nocturne.Core.Models.Queries.FindQuery.IncludesDeletedWhenNamed"/>).
    /// </summary>
    public bool IncludesDeletedWhenNamed { get; init; } = true;

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

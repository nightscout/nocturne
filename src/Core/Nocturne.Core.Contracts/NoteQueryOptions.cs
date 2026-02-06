using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts;

/// <summary>
/// Query options for filtering and paginating notes
/// </summary>
public class NoteQueryOptions
{
    /// <summary>
    /// Gets or sets the user ID to filter notes by
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Gets or sets the optional category filter
    /// </summary>
    public NoteCategory? Category { get; set; }

    /// <summary>
    /// Gets or sets the optional archive status filter
    /// </summary>
    public bool? IsArchived { get; set; }

    /// <summary>
    /// Gets or sets the optional tracker definition ID filter to find notes linked to a specific tracker
    /// </summary>
    public Guid? TrackerDefinitionId { get; set; }

    /// <summary>
    /// Gets or sets the optional state span ID filter to find notes linked to a specific state span
    /// </summary>
    public Guid? StateSpanId { get; set; }

    /// <summary>
    /// Gets or sets the optional start date filter (inclusive)
    /// </summary>
    public DateTime? FromDate { get; set; }

    /// <summary>
    /// Gets or sets the optional end date filter (inclusive)
    /// </summary>
    public DateTime? ToDate { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of notes to return
    /// </summary>
    public int? Limit { get; set; }

    /// <summary>
    /// Gets or sets the number of notes to skip for pagination
    /// </summary>
    public int? Offset { get; set; }
}

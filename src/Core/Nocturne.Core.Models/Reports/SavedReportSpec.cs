namespace Nocturne.Core.Models.Reports;

/// <summary>
/// A custom report a member saved: a name and the report spec as stored, which the API validates
/// and rewrites to the current spec version when it reads it.
/// </summary>
/// <param name="Id">The row's id (UUID v7).</param>
/// <param name="Name">Unique among the tenant's live custom reports.</param>
/// <param name="SpecJson">The normalised spec, as JSON, in the version <paramref name="Version"/> describes.</param>
/// <param name="Version">The spec format version <paramref name="SpecJson"/> was saved in.</param>
/// <param name="CreatedBySubjectId">The member who saved it first; null when no member was known.</param>
public sealed record SavedReportSpec(
    Guid Id,
    string Name,
    string SpecJson,
    int Version,
    Guid? CreatedBySubjectId,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>What a write to a saved report did.</summary>
public enum SavedReportSpecWriteStatus
{
    Saved,
    NotFound,

    /// <summary>Another live custom report of the tenant has the name.</summary>
    NameTaken,
}

/// <summary>A write's outcome, and the row as saved when it was.</summary>
public sealed record SavedReportSpecWrite(SavedReportSpecWriteStatus Status, SavedReportSpec? Report = null);

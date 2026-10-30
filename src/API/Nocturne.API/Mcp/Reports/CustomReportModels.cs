using System.Text.Json;

namespace Nocturne.API.Mcp.Reports;

/// <summary>A saved custom report as the list shows it.</summary>
/// <param name="CanEdit">Whether the caller may rename and delete it (<see cref="CustomReportEditor"/>).</param>
public sealed record CustomReportSummary(Guid Id, string Name, DateTime UpdatedAt, int PanelCount, bool CanEdit);

/// <summary>
/// The caller a write is checked against: its subject, and whether it may change any member's
/// report (an owner or a holder of <c>tenant.settings</c>). Anyone else changes only the reports
/// they created.
/// </summary>
public sealed record CustomReportEditor(Guid? SubjectId, bool EditsAll)
{
    public bool CanEdit(Guid? createdBy) => EditsAll || (SubjectId is { } subject && createdBy == subject);
}

/// <summary>One thing wrong with a spec: where (<c>panels[1].bindings.y</c>) and what to change.</summary>
public sealed record CustomReportSpecError(string Path, string Message);

/// <summary>A saved custom report.</summary>
/// <param name="Spec">
/// The spec rewritten to the current version and normalised; as stored when it is no longer valid,
/// and then <paramref name="Errors"/> says why.
/// </param>
/// <param name="CanEdit">Whether the caller may rename and delete it.</param>
public sealed record CustomReport(
    Guid Id,
    string Name,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    JsonElement Spec,
    IReadOnlyList<CustomReportSpecError> Errors,
    bool CanEdit);

/// <summary>A spec to run: as JSON, or as the encoded form a <c>?spec=</c> link carries. Send one.</summary>
public sealed record CustomReportRunRequest
{
    public JsonElement? Spec { get; init; }

    /// <summary>The <c>spec</c> query value of a <c>/reports/custom/new</c> link, as it is in the link.</summary>
    public string? Encoded { get; init; }
}

/// <summary>
/// A run: the report when the spec is valid, else the errors and, when it could be read, the spec
/// as sent.
/// </summary>
/// <param name="Result">The report result (the shape of the MCP tools' <c>structuredContent</c>).</param>
/// <param name="Spec">The spec that ran, normalised; or, when it did not, the spec as sent.</param>
public sealed record CustomReportRunResponse(
    JsonElement? Result,
    JsonElement? Spec,
    IReadOnlyList<CustomReportSpecError> Errors);

public sealed record CustomReportCreateRequest
{
    public string Name { get; init; } = string.Empty;

    public JsonElement? Spec { get; init; }
}

/// <summary>A rename, a new spec, or both; a part left out is unchanged.</summary>
public sealed record CustomReportUpdateRequest
{
    public string? Name { get; init; }

    public JsonElement? Spec { get; init; }
}

public enum CustomReportWriteStatus
{
    Saved,
    Invalid,
    NotFound,
    NameTaken,

    /// <summary>The caller may not change this report: it is another member's.</summary>
    Forbidden,
}

public sealed record CustomReportWrite(
    CustomReportWriteStatus Status,
    CustomReport? Report,
    IReadOnlyList<CustomReportSpecError> Errors);

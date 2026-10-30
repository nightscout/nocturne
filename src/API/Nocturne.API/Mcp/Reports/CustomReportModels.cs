using System.Text.Json;

namespace Nocturne.API.Mcp.Reports;

/// <summary>A saved custom report as the list shows it.</summary>
public sealed record CustomReportSummary(Guid Id, string Name, DateTime UpdatedAt, int PanelCount);

/// <summary>One thing wrong with a spec: where (<c>panels[1].bindings.y</c>) and what to change.</summary>
public sealed record CustomReportSpecError(string Path, string Message);

/// <summary>A saved custom report.</summary>
/// <param name="Spec">
/// The spec rewritten to the current version and normalised; as stored when it is no longer valid,
/// and then <paramref name="Errors"/> says why.
/// </param>
public sealed record CustomReport(
    Guid Id,
    string Name,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    JsonElement Spec,
    IReadOnlyList<CustomReportSpecError> Errors);

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

    public JsonElement Spec { get; init; }
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
}

public sealed record CustomReportWrite(
    CustomReportWriteStatus Status,
    CustomReport? Report,
    IReadOnlyList<CustomReportSpecError> Errors);

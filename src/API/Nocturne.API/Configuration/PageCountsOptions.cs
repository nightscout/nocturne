namespace Nocturne.API.Configuration;

/// <summary>
/// Whether the instance counts how often each page of the web app is opened. Bound to the
/// <c>Telemetry:PageCounts</c> configuration section. Off unless the operator turns it on; while
/// off, a page-view beacon changes nothing. See <c>docs/operations/page-counts.md</c>.
/// </summary>
public sealed class PageCountsOptions
{
    public const string SectionName = "Telemetry:PageCounts";

    public bool Enabled { get; set; }
}

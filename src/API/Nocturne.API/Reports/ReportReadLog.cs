namespace Nocturne.API.Reports;

/// <summary>
/// What a custom report run read, by category and record count, for its read-audit row
/// (<see cref="ReportReadAudit"/>). One per request scope; the datasets fill it as they read.
/// </summary>
internal sealed class ReportReadLog
{
    private readonly List<string> _datasets = [];

    public IReadOnlyList<string> Datasets => _datasets;

    public int RecordCount { get; private set; }

    public void Record(string dataset, int records)
    {
        if (!_datasets.Contains(dataset))
            _datasets.Add(dataset);
        RecordCount += records;
    }
}

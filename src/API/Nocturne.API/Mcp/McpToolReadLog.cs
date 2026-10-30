namespace Nocturne.API.Mcp;

/// <summary>
/// What a tool call or a web report run read, for its read-audit row. One per request scope, and a
/// stateless MCP request carries one call, so the tool fills it and <see cref="McpToolGuard"/>
/// writes it; a custom report run writes its own (<c>CustomReportsController.Run</c>).
/// </summary>
internal sealed class McpToolReadLog
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

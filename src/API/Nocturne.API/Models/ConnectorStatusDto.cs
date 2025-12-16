namespace Nocturne.API.Models;

public class ConnectorStatusDto
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string Status { get; set; }
    public string? Description { get; set; }
    public long TotalEntries { get; set; }
    public DateTime? LastEntryTime { get; set; }
    public int EntriesLast24Hours { get; set; }
    public string State { get; set; } = "Idle";
    public string? StateMessage { get; set; }
    public bool IsHealthy { get; set; }
}

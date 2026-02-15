namespace Nocturne.Connectors.Core.Models;

public class SyncRequest
{
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
    public List<SyncDataType> DataTypes { get; set; } = [];
}
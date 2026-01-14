namespace Nocturne.Connectors.MyLife.Models;

public class MyLifePatient
{
    public string? OnlinePatientId { get; set; }
    public string? OnlineUserId { get; set; }
    public string? EmailNewPatient { get; set; }
    public string? OwnerId { get; set; }
    public int Access { get; set; }
    public bool NotExisting { get; set; }
    public bool IsNew { get; set; }
    public bool NotDeleted { get; set; }
}
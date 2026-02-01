namespace Nocturne.Connectors.MyLife.Models;

public abstract class MyLifePatient(string? onlinePatientId)
{
    public string? OnlinePatientId { get; } = onlinePatientId;
}
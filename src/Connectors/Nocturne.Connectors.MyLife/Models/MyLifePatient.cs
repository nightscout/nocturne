using System.Text.Json.Serialization;

namespace Nocturne.Connectors.MyLife.Models;

public class MyLifePatient
{
    [JsonPropertyName("OnlinePatientId")]
    public string? OnlinePatientId { get; set; }
}
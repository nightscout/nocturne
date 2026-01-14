namespace Nocturne.Connectors.MyLife.Models;

public class MyLifeEvent
{
    public string? ETag { get; set; }
    public bool Succeeded { get; set; }
    public string? Message { get; set; }
    public string? FileEventId { get; set; }
    public int? IndexOnDevice { get; set; }
    public string? DeviceId { get; set; }
    public bool IsRunning { get; set; }
    public bool NotForIOB { get; set; }
    public string? EventId { get; set; }
    public long EventDateTime { get; set; }
    public long LastEditTimeStamp { get; set; }
    public bool EventSourceIsManualEntry { get; set; }
    public int EventTypeId { get; set; }
    public string? InformationFromDevice { get; set; }
    public string? PatientId { get; set; }
    public string? UserComment { get; set; }
    public string? Value { get; set; }
    public string? Marker { get; set; }
    public bool Deleted { get; set; }
    public int BgValueType { get; set; }
    public long CRC32Checksum { get; set; }
}
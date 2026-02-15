namespace Nocturne.Core.Models.V4;

/// <summary>
/// Normalized uploader/phone status snapshot extracted from DeviceStatus.
/// Fully typed - no JSONB blobs needed.
/// </summary>
public class UploaderSnapshot
{
    public Guid Id { get; set; }
    public long Mills { get; set; }
    public int? UtcOffset { get; set; }
    public string? Device { get; set; }
    public string? LegacyId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ModifiedAt { get; set; }

    public string? Name { get; set; }
    public int? Battery { get; set; }
    public double? BatteryVoltage { get; set; }
    public bool? IsCharging { get; set; }
    public double? Temperature { get; set; }
    public string? Type { get; set; }
}

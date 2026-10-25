using System.Text.Json.Serialization;
using Nocturne.Core.Models.Attributes;

namespace Nocturne.Core.Models;

/// <summary>
/// Step count (PebbleMovement) record, compatible with xDrip step count uploads.
/// Step count data is time-series movement data typically sourced from wearable devices via xDrip.
/// </summary>
/// <seealso cref="HeartRate"/>
/// <seealso cref="ProcessableDocumentBase"/>
public class StepCount : ProcessableDocumentBase
{
    /// <summary>
    /// MongoDB ObjectId
    /// </summary>
    [JsonPropertyName("_id")]
    public override string? Id { get; set; }

    /// <summary>
    /// ISO 8601 formatted creation timestamp
    /// </summary>
    [JsonPropertyName("created_at")]
    public override string? CreatedAt { get; set; } =
        DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    /// <summary>
    /// Timestamp in milliseconds since Unix epoch.
    /// Setting Mills converts the value to Timestamp internally (v1 compatibility).
    /// </summary>
    [JsonPropertyName("mills")]
    public override long Mills
    {
        get => Timestamp.ToMills();
        set => Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(value).UtcDateTime;
    }

    /// <summary>
    /// Canonical timestamp as UTC DateTime (source of truth)
    /// </summary>
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// UTC offset in minutes
    /// </summary>
    [JsonPropertyName("utcOffset")]
    public override int? UtcOffset { get; set; }

    /// <summary>
    /// Step count or movement metric value.
    /// May be an absolute total or a delta depending on <see cref="Source"/>.
    /// </summary>
    [JsonPropertyName("metric")]
    public int Metric { get; set; }

    /// <summary>
    /// Source bitmask.
    /// Bit 0 (value 1) indicates an absolute step count total; otherwise the value is a delta.
    /// Either way, step totals and chart bubbles add the metric up. Bit 1
    /// (<see cref="PossibleRunningTotalFlag"/>) marks a metric they leave out.
    /// </summary>
    [JsonPropertyName("source")]
    public int Source { get; set; }

    /// <summary>
    /// <see cref="Source"/> bit for a metric that may be a running counter reading rather than a
    /// count for its own interval, so adding it up would overcount. xDrip's <c>steps-total</c>
    /// uploads carry it. xDrip stores both kinds but uploads nothing that tells them apart, not
    /// even its own flag, which sets bit 0 on the counts xDrip adds up, the reverse of the bit 0
    /// meaning above.
    /// </summary>
    public const int PossibleRunningTotalFlag = 2;

    /// <summary>
    /// Whether <see cref="Source"/> has <see cref="PossibleRunningTotalFlag"/> set, so step totals
    /// and chart bubbles must skip this record.
    /// </summary>
    public bool IsPossibleRunningTotal() => (Source & PossibleRunningTotalFlag) != 0;

    /// <summary>
    /// The legacy <c>type</c> the record was uploaded with (xDrip sends <c>steps-total</c>), returned on
    /// the v1 activity read-back.
    /// </summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>
    /// Device identifier that recorded this reading
    /// </summary>
    [JsonPropertyName("device")]
    [Sanitizable]
    public string? Device { get; set; }

    [JsonPropertyName("enteredBy")]
    [Sanitizable]
    public string? EnteredBy { get; set; }

    /// <summary>
    /// Data source identifier indicating where this reading originated from
    /// </summary>
    [JsonPropertyName("data_source")]
    [NocturneOnly]
    public string? DataSource { get; set; }

    /// <summary>
    /// Stable per-source identifier for synchronization. When paired with <see cref="DataSource"/>,
    /// re-uploading the same measurement updates the existing record in place rather than duplicating it.
    /// </summary>
    [JsonPropertyName("syncIdentifier")]
    [NocturneOnly]
    public string? SyncIdentifier { get; set; }
}

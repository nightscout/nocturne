using System.Text.Json.Serialization;

namespace Nocturne.API.Services.Glucose;

/// <summary>
/// Pre-resolved therapy profile flattened into contiguous absolute-time segments covering
/// <c>[now, now+24h)</c> for on-device oref prediction. Property names are pinned to snake_case
/// with <see cref="JsonPropertyNameAttribute"/> so the wire contract is independent of any ambient
/// serializer naming policy, and every field is emitted (including zeros) so the client's strict
/// decoder never sees a missing key.
/// </summary>
/// <remarks>
/// Clients must additionally apply the fixed oref constants <c>max_iob=10</c>, <c>max_basal=4</c>,
/// <c>max_daily_basal=2</c> (see <c>PredictionService.GetProfileAsync</c>) to match a server-side run.
/// </remarks>
public sealed class ProfileSnapshotResponse
{
    /// <summary>When the snapshot was resolved (Unix ms); equals the window start.</summary>
    [JsonPropertyName("fetched_at_mills")]
    public long FetchedAtMills { get; set; }

    /// <summary>Ascending, contiguous, gap/overlap-free segments covering [now, now+24h).</summary>
    [JsonPropertyName("segments")]
    public List<ProfileSnapshotSegment> Segments { get; set; } = new();
}

/// <summary>
/// One flat segment of the resolved profile: all scalars are constant over <c>[start, end)</c>.
/// </summary>
public sealed class ProfileSnapshotSegment
{
    /// <summary>Segment start (Unix ms, inclusive).</summary>
    [JsonPropertyName("start_mills")] public long StartMills { get; set; }

    /// <summary>Segment end (Unix ms, exclusive).</summary>
    [JsonPropertyName("end_mills")] public long EndMills { get; set; }

    /// <summary>Duration of insulin action (hours).</summary>
    [JsonPropertyName("dia")] public double Dia { get; set; }

    /// <summary>Scheduled basal rate (U/hr).</summary>
    [JsonPropertyName("basal")] public double Basal { get; set; }

    /// <summary>Insulin sensitivity (mg/dL per U).</summary>
    [JsonPropertyName("sens")] public double Sens { get; set; }

    /// <summary>Carb ratio (g/U).</summary>
    [JsonPropertyName("carb_ratio")] public double CarbRatio { get; set; }

    /// <summary>Low BG target (mg/dL).</summary>
    [JsonPropertyName("min_bg")] public double MinBg { get; set; }

    /// <summary>High BG target (mg/dL).</summary>
    [JsonPropertyName("max_bg")] public double MaxBg { get; set; }

    /// <summary>Insulin activity peak (minutes).</summary>
    [JsonPropertyName("peak")] public int Peak { get; set; }

    /// <summary>Insulin activity curve model name (e.g. "rapid-acting").</summary>
    [JsonPropertyName("curve")] public string Curve { get; set; } = "rapid-acting";
}

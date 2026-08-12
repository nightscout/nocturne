namespace Nocturne.Core.Models.Analytics;

/// <summary>
/// Two CGM readings, one from each compared device, matched to the same moment in time.
/// </summary>
public sealed class CgmPairedReading
{
    /// <summary>Timestamp of device A's reading (UTC).</summary>
    public DateTime TimestampA { get; set; }

    /// <summary>Timestamp of the device B reading matched to it (UTC).</summary>
    public DateTime TimestampB { get; set; }

    /// <summary>Device A's glucose value (mg/dL).</summary>
    public double MgdlA { get; set; }

    /// <summary>Device B's glucose value (mg/dL).</summary>
    public double MgdlB { get; set; }
}

/// <summary>
/// Agreement measures over a paired CGM series.
/// </summary>
/// <remarks>
/// Device B is the reference: every relative measure divides by device B's value, and the signed
/// bias is expressed as A minus B. Which device is B is the caller's choice, so a comparison run
/// both ways is expected to produce different relative figures.
/// </remarks>
public sealed class CgmAgreementMetrics
{
    /// <summary>Number of readings pairs the measures were computed over.</summary>
    public int PairCount { get; set; }

    /// <summary>Mean of <c>|A - B|</c> across the pairs (mg/dL).</summary>
    public double MeanAbsoluteDifferenceMgdl { get; set; }

    /// <summary>Mean absolute relative difference: mean of <c>|A - B| / B</c>, as a percentage.</summary>
    public double MardPercent { get; set; }

    /// <summary>Mean of <c>A - B</c> across the pairs (mg/dL); negative means A reads lower than B.</summary>
    public double BiasMgdl { get; set; }

    /// <summary>
    /// Percentage of pairs within 15 mg/dL of the reference when the reference is below
    /// 100 mg/dL, or within 15% of it otherwise.
    /// </summary>
    public double Within15Percent { get; set; }
}

/// <summary>
/// Result of time-pairing two CGM devices' readings over a window, with the paired series and its
/// agreement measures.
/// </summary>
public sealed class CgmComparisonResult
{
    /// <summary>Registered device compared as A.</summary>
    public Guid DeviceAId { get; set; }

    /// <summary>Display name of device A.</summary>
    public string DeviceAName { get; set; } = string.Empty;

    /// <summary>Registered device compared as B, the reference for relative measures.</summary>
    public Guid DeviceBId { get; set; }

    /// <summary>Display name of device B.</summary>
    public string DeviceBName { get; set; } = string.Empty;

    /// <summary>Inclusive UTC start of the compared window.</summary>
    public DateTime StartDate { get; set; }

    /// <summary>Exclusive UTC end of the compared window.</summary>
    public DateTime EndDate { get; set; }

    /// <summary>Maximum time difference, in minutes, at which two readings were matched.</summary>
    public double ToleranceMinutes { get; set; }

    /// <summary>Device A readings available in the window.</summary>
    public int ReadingCountA { get; set; }

    /// <summary>Device B readings available in the window.</summary>
    public int ReadingCountB { get; set; }

    /// <summary>Device A readings with no device B reading inside the tolerance.</summary>
    public int UnpairedCountA { get; set; }

    /// <summary>Device B readings never matched to a device A reading.</summary>
    public int UnpairedCountB { get; set; }

    /// <summary>The paired series, ordered by <see cref="CgmPairedReading.TimestampA"/>.</summary>
    public List<CgmPairedReading> Pairs { get; set; } = [];

    /// <summary>
    /// Agreement measures over <see cref="Pairs"/>, or <c>null</c> when nothing paired — zeroed
    /// measures would otherwise read as exact agreement.
    /// </summary>
    public CgmAgreementMetrics? Metrics { get; set; }
}

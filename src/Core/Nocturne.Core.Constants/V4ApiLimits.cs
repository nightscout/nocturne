namespace Nocturne.Core.Constants;

/// <summary>Limits of the V4 API that its own clients, such as a remote Nocturne connector, must respect.</summary>
public static class V4ApiLimits
{
    /// <summary>
    /// Maximum span, in days, between the <c>from</c> and <c>to</c> bounds of a V4 range read.
    /// </summary>
    /// <remarks>
    /// The widest range the report pages offer is 90 days, but their date picker also takes an
    /// arbitrary custom range, so the ceiling sits far enough above the presets to leave the picker
    /// usable. A range with only one bound set is not spanned and is left alone.
    /// </remarks>
    public const int MaxDateSpanDays = 366;
}

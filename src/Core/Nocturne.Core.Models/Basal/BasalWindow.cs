namespace Nocturne.Core.Models.Basal;

/// <summary>
/// A half-open time window in Unix milliseconds: <c>[StartMills, EndMills)</c>.
/// </summary>
/// <param name="StartMills">Window start, inclusive.</param>
/// <param name="EndMills">Window end, exclusive.</param>
public readonly record struct BasalWindow(long StartMills, long EndMills)
{
    /// <summary>Window length in ms. Zero or negative if the window is empty/inverted.</summary>
    public long DurationMills => EndMills - StartMills;

    /// <summary>True when the window has positive duration.</summary>
    public bool IsValid => EndMills > StartMills;
}

namespace Nocturne.Core.Models;

/// <summary>
/// The confidence boundaries for compression low suggestions. Detection keeps a candidate at
/// <see cref="MinThreshold"/> and above; a rescale of the score moves both constants together.
/// </summary>
public static class CompressionLowConfidence
{
    public const double MinThreshold = 0.65;
    public const double High = 0.75;

    public static CompressionLowConfidenceBand BandOf(double confidence) =>
        confidence >= High ? CompressionLowConfidenceBand.High : CompressionLowConfidenceBand.Medium;
}

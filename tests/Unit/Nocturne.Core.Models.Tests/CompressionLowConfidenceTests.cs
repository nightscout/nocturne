using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Nocturne.Core.Models.Tests;

public class CompressionLowConfidenceTests
{
    [Fact]
    public void BandOf_isMediumAtTheDetectionThreshold()
    {
        CompressionLowConfidence.BandOf(CompressionLowConfidence.MinThreshold)
            .Should().Be(CompressionLowConfidenceBand.Medium);
    }

    [Fact]
    public void BandOf_isHighAtTheHighBoundary()
    {
        CompressionLowConfidence.BandOf(CompressionLowConfidence.High)
            .Should().Be(CompressionLowConfidenceBand.High);
    }

    [Fact]
    public void BandOf_isMediumJustBelowTheHighBoundary()
    {
        CompressionLowConfidence.BandOf(double.BitDecrement(CompressionLowConfidence.High))
            .Should().Be(CompressionLowConfidenceBand.Medium);
    }

    [Fact]
    public void BandOf_isMediumForAStoredScoreBelowTheThreshold()
    {
        CompressionLowConfidence.BandOf(CompressionLowConfidence.MinThreshold - 0.2)
            .Should().Be(CompressionLowConfidenceBand.Medium);
    }

    [Theory]
    [InlineData(0.65, CompressionLowConfidenceBand.Medium)]
    [InlineData(0.7499, CompressionLowConfidenceBand.Medium)]
    [InlineData(0.75, CompressionLowConfidenceBand.High)]
    [InlineData(1.0, CompressionLowConfidenceBand.High)]
    public void BandOf_followsTheAgreedBoundaries(double confidence, CompressionLowConfidenceBand band)
    {
        CompressionLowConfidence.BandOf(confidence).Should().Be(band);
    }

    [Fact]
    public void BoundariesAreTheAgreedValues()
    {
        CompressionLowConfidence.MinThreshold.Should().Be(0.65);
        CompressionLowConfidence.High.Should().Be(0.75);
    }

    [Fact]
    public void HighBoundaryClearsTheDetectionThreshold()
    {
        CompressionLowConfidence.High.Should().BeGreaterThan(CompressionLowConfidence.MinThreshold);
    }

    [Theory]
    [InlineData(0.9, "High")]
    [InlineData(0.7, "Medium")]
    public void Suggestion_servesItsBandAsAString(double confidence, string band)
    {
        var json = JsonSerializer.Serialize(new CompressionLowSuggestion { Confidence = confidence });

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("confidenceBand").GetString().Should().Be(band);
    }
}

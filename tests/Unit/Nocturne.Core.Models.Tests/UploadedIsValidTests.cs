using System.Text.Json;
using FluentAssertions;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.Core.Models.Tests;

/// <summary>
/// A treatment uploaded with <c>isValid: false</c> is stored deleted, so the span and temp basal
/// upload fields keep only <c>isValid: true</c>. A <c>false</c> a record kept before still reads back.
/// </summary>
[Trait("Category", "Unit")]
public class UploadedIsValidTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, null)]
    [InlineData(null, null)]
    public void A_span_keeps_isValid_only_when_true(bool? uploaded, bool? kept)
    {
        var metadata = new Dictionary<string, object>();
        SpanUploadedFields.Keep(metadata, new Treatment { IsValid = uploaded });

        var served = new Treatment();
        SpanUploadedFields.Restore(served, metadata);

        served.IsValid.Should().Be(kept);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, null)]
    [InlineData(null, null)]
    public void A_temp_basal_keeps_isValid_only_when_true(bool? uploaded, bool? kept)
    {
        var record = TempBasalUploadedFields.Keep(null, new Treatment { IsValid = uploaded });

        var served = new Treatment();
        TempBasalUploadedFields.Restore(served, record);

        served.IsValid.Should().Be(kept);
    }

    [Fact]
    public void A_span_written_with_isValid_false_still_reads_back_false()
    {
        var served = new Treatment();
        SpanUploadedFields.Restore(
            served, new Dictionary<string, object> { ["isValid"] = JsonDocument.Parse("false").RootElement.Clone() });

        served.IsValid.Should().BeFalse();
    }

    [Fact]
    public void A_temp_basal_written_with_isValid_false_still_reads_back_false()
    {
        var served = new Treatment();
        TempBasalUploadedFields.Restore(served, new Dictionary<string, object?> { ["isValid"] = false });

        served.IsValid.Should().BeFalse();
    }
}

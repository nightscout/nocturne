using FluentAssertions;
using Nocturne.Core.Models.Configuration;
using Xunit;

namespace Nocturne.Core.Models.Tests.Configuration;

public class GlucoseUnitDefaultsTests
{
    [Theory]
    [InlineData("en-US", "mg/dl")]
    [InlineData("en-AU", "mmol")]
    [InlineData("en_CA", "mmol")]
    [InlineData("fr-FR", "mg/dl")]
    [InlineData("sv-SE", "mmol")]
    [InlineData("zh-Hant-TW", "mg/dl")]
    [InlineData("zh-Hans-CN", "mmol")]
    [InlineData("en", "mg/dl")]
    [InlineData("de", "mg/dl")]
    [InlineData("sv", "mmol")]
    [InlineData("nl", "mmol")]
    [InlineData("fi", "mmol")]
    [InlineData("ru", "mmol")]
    [InlineData("zh", "mmol")]
    [InlineData("xx", "mg/dl")]
    [InlineData("", "mg/dl")]
    [InlineData(null, "mg/dl")]
    public void ForLocale_DefaultsByRegion(string? locale, string expected)
    {
        GlucoseUnitDefaults.ForLocale(locale).Should().Be(expected);
    }

    [Theory]
    [InlineData("mmol", "mmol")]
    [InlineData("mmol/L", "mmol")]
    [InlineData("MMOL/l", "mmol")]
    [InlineData("mg/dL", "mg/dl")]
    [InlineData("mgdl", "mg/dl")]
    [InlineData("mg/dl", "mg/dl")]
    [InlineData("g/L", null)]
    [InlineData(" ", null)]
    [InlineData(null, null)]
    public void Normalize_ReadsTheSpellingsNightscoutUses(string? units, string? expected)
    {
        GlucoseUnitDefaults.Normalize(units).Should().Be(expected);
    }
}

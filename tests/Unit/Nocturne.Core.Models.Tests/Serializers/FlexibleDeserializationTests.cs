using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Serializers;
using Xunit;

namespace Nocturne.Core.Models.Tests.Serializers;

/// <summary>
/// Tests that Entry and Treatment models correctly deserialize non-numeric string values
/// found in real-world OpenAPS Data Commons data.
/// </summary>
public class FlexibleDeserializationTests
{
    // ========================================================================
    // Entry.Noise — real data contains "Clean" instead of a number
    // ========================================================================

    [Fact]
    public void Entry_Noise_DeserializesNumericValue()
    {
        var json = """{"noise": 1}""";
        var entry = JsonSerializer.Deserialize<Entry>(json);

        entry!.Noise.Should().Be(1);
    }

    [Fact]
    public void Entry_Noise_DeserializesCleanStringAsNull()
    {
        var json = """{"noise": "Clean"}""";
        var entry = JsonSerializer.Deserialize<Entry>(json);

        entry!.Noise.Should().BeNull();
    }

    [Fact]
    public void Entry_Noise_DeserializesNumericStringAsInt()
    {
        var json = """{"noise": "3"}""";
        var entry = JsonSerializer.Deserialize<Entry>(json);

        entry!.Noise.Should().Be(3);
    }

    [Fact]
    public void Entry_Noise_DeserializesNullAsNull()
    {
        var json = """{"noise": null}""";
        var entry = JsonSerializer.Deserialize<Entry>(json);

        entry!.Noise.Should().BeNull();
    }

    [Fact]
    public void Entry_Noise_SerializesAsNumber()
    {
        var entry = new Entry { Noise = 2 };
        var json = JsonSerializer.Serialize(entry);

        json.Should().Contain("\"noise\":2");
    }

    // ========================================================================
    // Treatment.Rate — real data contains "offset" instead of a number
    // ========================================================================

    [Fact]
    public void Treatment_Rate_DeserializesNumericValue()
    {
        var json = """{"rate": 1.5}""";
        var treatment = JsonSerializer.Deserialize<Treatment>(json);

        treatment!.Rate.Should().Be(1.5);
    }

    [Fact]
    public void Treatment_Rate_DeserializesOffsetStringAsNull()
    {
        var json = """{"rate": "offset"}""";
        var treatment = JsonSerializer.Deserialize<Treatment>(json);

        treatment!.Rate.Should().BeNull();
    }

    [Fact]
    public void Treatment_Rate_DeserializesNumericStringAsDouble()
    {
        var json = """{"rate": "0.75"}""";
        var treatment = JsonSerializer.Deserialize<Treatment>(json);

        treatment!.Rate.Should().Be(0.75);
    }

    [Fact]
    public void Treatment_Rate_DeserializesNullAsNull()
    {
        var json = """{"rate": null}""";
        var treatment = JsonSerializer.Deserialize<Treatment>(json);

        treatment!.Rate.Should().BeNull();
    }

    // ========================================================================
    // Number converters — AAPS writes fractional values into integer wire fields,
    // and one unrepresentable value must never fail the payload around it
    // ========================================================================

    private static readonly JsonSerializerOptions NumberOptions = new()
    {
        Converters =
        {
            new FlexibleIntConverter(),
            new FlexibleNullableIntConverter(),
            new FlexibleLongConverter(),
            new FlexibleNullableLongConverter(),
            new FlexibleDoubleConverter(),
            new FlexibleNullableDoubleConverter(),
            new FlexibleDecimalConverter(),
            new FlexibleNullableDecimalConverter(),
        },
    };

    [Theory]
    [InlineData("29.999999966", 30)]
    [InlineData("-29.999999966", -30)]
    [InlineData("109.4", 109)]
    [InlineData("30", 30)]
    [InlineData("\"30\"", 30)]
    [InlineData("\"29.999999966\"", 30)]
    [InlineData("\"109.4\"", 109)]
    [InlineData("2147483647", 2147483647)]
    [InlineData("\"2147483647\"", 2147483647)]
    [InlineData("2147483648", null)]
    [InlineData("\"2147483648\"", null)]
    [InlineData("-2147483649", null)]
    [InlineData("\"-2147483649\"", null)]
    [InlineData("\"\"", null)]
    [InlineData("\"   \"", null)]
    [InlineData("\"Clean\"", null)]
    [InlineData("null", null)]
    [InlineData("true", null)]
    [InlineData("1e12", null)]
    [InlineData("-1e12", null)]
    [InlineData("\"1e12\"", null)]
    [InlineData("\"1,234\"", null)]
    [InlineData("{}", null)]
    [InlineData("[]", null)]
    public void NullableInt_CoercesEveryWireForm(string jsonValue, int? expected)
    {
        var result = JsonSerializer.Deserialize<int?>(jsonValue, NumberOptions);

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("29.999999966", 30)]
    [InlineData("-29.999999966", -30)]
    [InlineData("109.4", 109)]
    [InlineData("30", 30)]
    [InlineData("\"30\"", 30)]
    [InlineData("\"29.999999966\"", 30)]
    [InlineData("2147483647", 2147483647)]
    [InlineData("2147483648", 0)]
    [InlineData("\"2147483648\"", 0)]
    [InlineData("-2147483649", 0)]
    [InlineData("\"-2147483649\"", 0)]
    [InlineData("\"\"", 0)]
    [InlineData("\"Clean\"", 0)]
    [InlineData("null", 0)]
    [InlineData("false", 0)]
    [InlineData("1e12", 0)]
    [InlineData("-1e12", 0)]
    [InlineData("\"1e12\"", 0)]
    [InlineData("\"1,234\"", 0)]
    [InlineData("{}", 0)]
    [InlineData("[]", 0)]
    public void Int_CoercesEveryWireForm(string jsonValue, int expected)
    {
        var result = JsonSerializer.Deserialize<int>(jsonValue, NumberOptions);

        result.Should().Be(expected);
    }

    /// <summary>
    /// <see cref="Math.Round(double)"/> is half-to-even, matching the write side's
    /// <c>RoundedNullableDoubleConverter</c>, so a midpoint does not always round up.
    /// </summary>
    [Theory]
    [InlineData("109.5", 110)]
    [InlineData("110.5", 110)]
    [InlineData("\"109.5\"", 110)]
    [InlineData("\"110.5\"", 110)]
    public void NullableInt_RoundsMidpointsToEven(string jsonValue, int expected)
    {
        var result = JsonSerializer.Deserialize<int?>(jsonValue, NumberOptions);

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("29.999999966", 30L)]
    [InlineData("\"29.999999966\"", 30L)]
    [InlineData("109.4", 109L)]
    [InlineData("110.5", 110L)]
    [InlineData("1700000000000", 1700000000000L)]
    [InlineData("\"1700000000000\"", 1700000000000L)]
    [InlineData("9223372036854775808", null)]
    [InlineData("1e30", null)]
    [InlineData("\"1e30\"", null)]
    [InlineData("\"1,234\"", null)]
    [InlineData("\"\"", null)]
    [InlineData("\"   \"", null)]
    [InlineData("null", null)]
    [InlineData("true", null)]
    [InlineData("false", null)]
    [InlineData("{}", null)]
    [InlineData("[]", null)]
    public void NullableLong_CoercesEveryWireForm(string jsonValue, long? expected)
    {
        var result = JsonSerializer.Deserialize<long?>(jsonValue, NumberOptions);

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("29.999999966", 30L)]
    [InlineData("\"29.999999966\"", 30L)]
    [InlineData("1700000000000", 1700000000000L)]
    [InlineData("9223372036854775808", 0L)]
    [InlineData("1e30", 0L)]
    [InlineData("\"1e30\"", 0L)]
    [InlineData("\"1,234\"", 0L)]
    [InlineData("\"   \"", 0L)]
    [InlineData("null", 0L)]
    [InlineData("true", 0L)]
    [InlineData("{}", 0L)]
    public void Long_CoercesEveryWireForm(string jsonValue, long expected)
    {
        var result = JsonSerializer.Deserialize<long>(jsonValue, NumberOptions);

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("1.5", 1.5)]
    [InlineData("\"1.5\"", 1.5)]
    [InlineData("1e30", 1e30)]
    [InlineData("1e400", null)]
    [InlineData("-1e400", null)]
    [InlineData("\"1e400\"", null)]
    [InlineData("\"1,234\"", null)]
    [InlineData("\"offset\"", null)]
    [InlineData("\"\"", null)]
    [InlineData("null", null)]
    [InlineData("{}", null)]
    [InlineData("[]", null)]
    public void NullableDouble_CoercesEveryWireForm(string jsonValue, double? expected)
    {
        var result = JsonSerializer.Deserialize<double?>(jsonValue, NumberOptions);

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("1.5", 1.5)]
    [InlineData("1e400", 0)]
    [InlineData("\"1e400\"", 0)]
    [InlineData("\"1,234\"", 0)]
    [InlineData("null", 0)]
    public void Double_CoercesEveryWireForm(string jsonValue, double expected)
    {
        var result = JsonSerializer.Deserialize<double>(jsonValue, NumberOptions);

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("29.5", 29.5)]
    [InlineData("\"29.5\"", 29.5)]
    [InlineData("1e40", null)]
    [InlineData("1e400", null)]
    [InlineData("\"1e40\"", null)]
    [InlineData("\"1,234\"", null)]
    [InlineData("\"\"", null)]
    [InlineData("null", null)]
    [InlineData("{}", null)]
    [InlineData("[]", null)]
    public void NullableDecimal_CoercesEveryWireForm(string jsonValue, double? expected)
    {
        var result = JsonSerializer.Deserialize<decimal?>(jsonValue, NumberOptions);

        result.Should().Be(expected.HasValue ? (decimal)expected.Value : null);
    }

    [Theory]
    [InlineData("29.5", 29.5)]
    [InlineData("1e40", 0)]
    [InlineData("\"1e40\"", 0)]
    [InlineData("\"1,234\"", 0)]
    [InlineData("null", 0)]
    public void Decimal_CoercesEveryWireForm(string jsonValue, double expected)
    {
        var result = JsonSerializer.Deserialize<decimal>(jsonValue, NumberOptions);

        result.Should().Be((decimal)expected);
    }

    [Fact]
    public void Entry_Noise_DeserializesFractionalNumberAsRoundedInt()
    {
        var json = """{"noise": 1.9999999}""";
        var entry = JsonSerializer.Deserialize<Entry>(json);

        entry!.Noise.Should().Be(2);
    }

    [Fact]
    public void NumberConverters_ParseStringsInvariantlyUnderACommaDecimalCulture()
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            JsonSerializer.Deserialize<int?>("\"29.999999966\"", NumberOptions).Should().Be(30);
            JsonSerializer.Deserialize<long?>("\"29.999999966\"", NumberOptions).Should().Be(30);
            JsonSerializer.Deserialize<double?>("\"1.5\"", NumberOptions).Should().Be(1.5);
            JsonSerializer.Deserialize<decimal?>("\"29.5\"", NumberOptions).Should().Be(29.5m);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}

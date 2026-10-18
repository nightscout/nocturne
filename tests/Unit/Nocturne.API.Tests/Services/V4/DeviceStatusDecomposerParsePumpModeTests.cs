using FluentAssertions;
using Nocturne.API.Services.V4;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Services.V4;

public class DeviceStatusDecomposerParsePumpModeTests
{
    [Theory]
    [InlineData("Automatic", PumpModeState.Automatic)]
    [InlineData("manual", PumpModeState.Manual)]
    public void ParsePumpMode_DefinedName_IgnoresCase(string input, PumpModeState expected)
    {
        DeviceStatusDecomposer.ParsePumpMode(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("Automatic, Manual")]
    [InlineData("Suspended")]
    [InlineData(null)]
    public void ParsePumpMode_NumericListOrOutOfScopeState_ReturnsNull(string? input)
    {
        DeviceStatusDecomposer.ParsePumpMode(input).Should().BeNull();
    }
}

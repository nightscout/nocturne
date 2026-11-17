using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.Core.Models.Tests;

public class CobResultDisplayTests
{
    [Theory]
    [InlineData(12.25, 12.3, "COB: 12.3g")]
    [InlineData(12.24, 12.2, "COB: 12.2g")]
    [InlineData(0.0, 0.0, "COB: 0g")]
    public void Display_IsOneDecimalRoundedHalfUp(double cob, double display, string displayLine)
    {
        var result = new CobResult { Cob = cob };

        Assert.Equal(display, result.Display);
        Assert.Equal(displayLine, result.DisplayLine);
    }
}

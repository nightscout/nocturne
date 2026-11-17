using System.Text.Json;
using FluentAssertions;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Core.Models.Tests;

public class PaginationInfoTests
{
    [Fact]
    public void NullTotal_IsOmittedFromJson()
    {
        var json = JsonSerializer.Serialize(new PaginationInfo(100, 0));

        json.Should().NotContain("otal");
    }

    [Fact]
    public void PresentTotal_IsWrittenToJson()
    {
        var json = JsonSerializer.Serialize(new PaginationInfo(100, 0, 0));

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.EnumerateObject().Single(p => p.Name.Equals("total", StringComparison.OrdinalIgnoreCase))
            .Value.GetInt32().Should().Be(0);
    }

    [Theory]
    [InlineData(100, 0, 0, 0)]
    [InlineData(100, 0, 99, 99)]
    public async Task ForPageAsync_ShortFirstPage_UsesPageLengthWithoutCounting(int limit, int offset, int returned, int expected)
    {
        var counted = false;

        var info = await PaginationInfo.ForPageAsync(limit, offset, returned, false, () => { counted = true; return Task.FromResult(-1); });

        info.Total.Should().Be(expected);
        counted.Should().BeFalse();
    }

    [Theory]
    [InlineData(100, 0, 100)]
    [InlineData(100, 100, 5)]
    public async Task ForPageAsync_OtherwiseWithoutIncludeTotal_LeavesTotalNullWithoutCounting(int limit, int offset, int returned)
    {
        var counted = false;

        var info = await PaginationInfo.ForPageAsync(limit, offset, returned, false, () => { counted = true; return Task.FromResult(-1); });

        info.Total.Should().BeNull();
        counted.Should().BeFalse();
    }

    [Fact]
    public async Task ForPageAsync_IncludeTotal_UsesTheCount()
    {
        var info = await PaginationInfo.ForPageAsync(100, 0, 3, true, () => Task.FromResult(42));

        info.Total.Should().Be(42);
    }
}

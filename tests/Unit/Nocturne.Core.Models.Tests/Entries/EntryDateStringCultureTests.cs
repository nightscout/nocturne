using System.Globalization;
using FluentAssertions;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.Core.Models.Tests.Entries;

[Collection(NonUtcHostCollection.Name)]
[Trait("Category", "Unit")]
public sealed class EntryDateStringCultureTests : IDisposable
{
    private const string Format = "yyyy-MM-ddTHH:mm:ss.fffZ";

    private static readonly long Mills =
        new DateTimeOffset(2026, 9, 26, 8, 5, 7, 123, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private readonly CultureInfo _previous = CultureInfo.CurrentCulture;

    public void Dispose() => CultureInfo.CurrentCulture = _previous;

    [Theory]
    [InlineData("fa-IR")]
    [InlineData("th-TH")]
    [InlineData("ar-SA")]
    public void DateString_IsInvariantUnderTheServerCulture(string culture)
    {
        CultureInfo.CurrentCulture = new CultureInfo(culture);
        DateTimeOffset.FromUnixTimeMilliseconds(Mills).ToString(Format)
            .Should().NotBe("2026-09-26T08:05:07.123Z");

        var entry = new Entry { Mills = Mills };

        entry.DateString.Should().Be("2026-09-26T08:05:07.123Z");
    }
}

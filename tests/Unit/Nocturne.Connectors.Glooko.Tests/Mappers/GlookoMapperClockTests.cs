using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Mappers;
using Nocturne.Connectors.Glooko.Models;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Mappers;

[Trait("Category", "Unit")]
public class GlookoMapperClockTests
{
    private static readonly DateTimeOffset Instant = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TempBasals_AreStampedWithTheClock()
    {
        var clock = new FakeTimeProvider(Instant);
        var batch = JsonSerializer.Deserialize<GlookoBatchData>(
            """{"temporaryBasals":[{"timestamp":"2026-02-20T12:00:00.000Z","duration":1800,"rate":0.5}]}""")!;
        var mapper = new GlookoTempBasalMapper(
            "glooko-connector",
            new GlookoTimeMapper(new GlookoConnectorConfiguration(), NullLogger.Instance),
            NullLogger.Instance,
            clock);

        var record = mapper.TransformV2ToTempBasals(batch).Should().ContainSingle().Subject;

        record.CreatedAt.Should().Be(Instant.UtcDateTime);
        record.ModifiedAt.Should().Be(Instant.UtcDateTime);
    }
}

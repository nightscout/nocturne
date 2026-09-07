using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Mappers;
using Nocturne.Connectors.Glooko.Models;
using Xunit;

namespace Nocturne.Connectors.Glooko.Tests.Mappers;

/// <summary>
/// Covers <see cref="GlookoNoteMapper.MapSsv2Notes"/> — the SSV2 <c>notes</c> feed (app-logged free-text
/// notes) → Note.
/// </summary>
[Trait("Category", "Unit")]
public class GlookoNoteMapperTests
{
    private readonly GlookoNoteMapper _mapper;

    public GlookoNoteMapperTests()
    {
        var logger = Mock.Of<ILogger>();
        var timeMapper = new GlookoTimeMapper(new GlookoConnectorConfiguration(), logger);
        _mapper = new GlookoNoteMapper("glooko-connector", timeMapper, logger);
    }

    private static GlookoSsv2Note Note(
        string? value,
        string? timestamp = "2023-06-20T12:55:00.000Z",
        string? guid = "C4FA0000-0000-0000-0000-000000000000",
        bool softDeleted = false) => new()
    {
        Value = value,
        Timestamp = timestamp,
        Guid = guid,
        SoftDeleted = softDeleted,
        ManuallyEnteredText = true,
    };

    [Fact]
    public void MapsTextAndTimestamp()
    {
        var notes = _mapper.MapSsv2Notes([Note("Versehentliche doppelte Eingabe der KH (16g)")]);

        notes.Should().ContainSingle();
        var n = notes[0];
        n.Text.Should().Be("Versehentliche doppelte Eingabe der KH (16g)");
        // Default config (offset 0, no timeline) → fake-UTC wall-clock preserved.
        n.Timestamp.Should().Be(new DateTime(2023, 6, 20, 12, 55, 0, DateTimeKind.Utc));
        n.DataSource.Should().Be("glooko-connector");
    }

    [Fact]
    public void KeysLegacyIdOnGuid_AndSetsSyncIdentifier()
    {
        var notes = _mapper.MapSsv2Notes([Note("hi", guid: "abc-123")]);

        notes[0].LegacyId.Should().Be("glooko_note_abc-123");
        notes[0].SyncIdentifier.Should().Be(notes[0].LegacyId);
    }

    [Fact]
    public void NoGuid_FallsBackToHashedLegacyId()
    {
        var notes = _mapper.MapSsv2Notes([Note("hi", guid: null)]);

        notes[0].LegacyId.Should().StartWith("glooko_").And.NotContain("note_");
    }

    [Fact]
    public void SkipsSoftDeletedAndEmptyText()
    {
        var notes = _mapper.MapSsv2Notes(
        [
            Note("deleted", softDeleted: true),
            Note(null),
            Note("   "),
            Note(""),
        ]);

        notes.Should().BeEmpty();
    }
}

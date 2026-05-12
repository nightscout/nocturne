using FluentAssertions;
using Nocturne.Connectors.Core.Utilities;
using Xunit;

namespace Nocturne.Connectors.Core.Tests.Utilities;

public class DateChunkerTests
{
    [Fact]
    public void Chunk_RangeSmallerThanChunkSize_ReturnsSingleChunk()
    {
        var from = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2025, 1, 5, 0, 0, 0, DateTimeKind.Utc);

        var chunks = DateChunker.Chunk(from, to, TimeSpan.FromDays(14)).ToList();

        chunks.Should().HaveCount(1);
        chunks[0].From.Should().Be(from);
        chunks[0].To.Should().Be(to);
    }

    [Fact]
    public void Chunk_RangeExactlyChunkSize_ReturnsSingleChunk()
    {
        var from = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2025, 1, 15, 0, 0, 0, DateTimeKind.Utc);

        var chunks = DateChunker.Chunk(from, to, TimeSpan.FromDays(14)).ToList();

        chunks.Should().HaveCount(1);
        chunks[0].From.Should().Be(from);
        chunks[0].To.Should().Be(to);
    }

    [Fact]
    public void Chunk_RangeLargerThanChunkSize_ReturnsMultipleChunks()
    {
        var from = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2025, 2, 1, 0, 0, 0, DateTimeKind.Utc); // 31 days

        var chunks = DateChunker.Chunk(from, to, TimeSpan.FromDays(14)).ToList();

        chunks.Should().HaveCount(3); // 14 + 14 + 3
        chunks[0].From.Should().Be(from);
        chunks[0].To.Should().Be(from.AddDays(14));
        chunks[1].From.Should().Be(from.AddDays(14));
        chunks[1].To.Should().Be(from.AddDays(28));
        chunks[2].From.Should().Be(from.AddDays(28));
        chunks[2].To.Should().Be(to);
    }

    [Fact]
    public void Chunk_FromEqualsTo_ReturnsSingleChunk()
    {
        var date = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var chunks = DateChunker.Chunk(date, date, TimeSpan.FromDays(14)).ToList();

        chunks.Should().HaveCount(1);
        chunks[0].From.Should().Be(date);
        chunks[0].To.Should().Be(date);
    }

    [Fact]
    public void Chunk_SixMonthRange_ReturnsExpectedChunkCount()
    {
        var from = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2025, 7, 1, 0, 0, 0, DateTimeKind.Utc); // 181 days

        var chunks = DateChunker.Chunk(from, to, TimeSpan.FromDays(14)).ToList();

        chunks.Should().HaveCount(13); // ceil(181/14) = 13
        chunks.First().From.Should().Be(from);
        chunks.Last().To.Should().Be(to);

        // Chunks should be contiguous (no gaps or overlaps)
        for (var i = 1; i < chunks.Count; i++)
            chunks[i].From.Should().Be(chunks[i - 1].To);
    }
}

using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;

namespace Nocturne.Infrastructure.Data.Tests.Extensions;

[Trait("Category", "Unit")]
public class ObservationWindowTests
{
    private static readonly DateTime From = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = From.AddHours(1);

    private sealed class Row : IObservationTimestamped
    {
        public DateTime Timestamp { get; set; }
        public DateTime Started { get; set; }
        public DateTimeOffset Analysed { get; set; }
        public long Mills { get; set; }
    }

    private static readonly DateTime[] Instants =
    [
        From.AddMilliseconds(-1), From, From.AddMinutes(30), To, To.AddMilliseconds(1),
    ];

    private static IQueryable<Row> RowsAtEach() =>
        Instants.Select(at => new Row { Timestamp = at, Started = DateTime.MinValue }).AsQueryable();

    [Fact]
    public void InWindow_IncludesBothBounds()
    {
        RowsAtEach().InWindow(From, To).Select(r => r.Timestamp)
            .Should().Equal(From, From.AddMinutes(30), To);
    }

    [Fact]
    public void InWindow_WithoutAnUpperBound_ReadsEverythingFromTheLowerBoundOn()
    {
        RowsAtEach().InWindow(From, null).Select(r => r.Timestamp)
            .Should().Equal(From, From.AddMinutes(30), To, To.AddMilliseconds(1));
    }

    [Fact]
    public void InWindow_WithoutALowerBound_ReadsEverythingUpToTheUpperBound()
    {
        RowsAtEach().InWindow(null, To).Select(r => r.Timestamp)
            .Should().Equal(From.AddMilliseconds(-1), From, From.AddMinutes(30), To);
    }

    [Fact]
    public void InWindow_WithoutBounds_ReadsEveryRow()
    {
        RowsAtEach().InWindow(null, null).Should().HaveCount(Instants.Length);
    }

    [Fact]
    public void InWindow_ByASelector_ReadsTheSelectedColumnAndNotTimestamp()
    {
        var rows = Instants
            .Select(at => new Row { Timestamp = DateTime.MinValue, Started = at })
            .AsQueryable();

        rows.InWindow(r => r.Started, From, To).Select(r => r.Started)
            .Should().Equal(From, From.AddMinutes(30), To);
        rows.InWindow(From, To).Should().BeEmpty();
    }

    [Fact]
    public void InWindow_ByASelector_ServesADateTimeOffsetAndAMillisecondColumnInclusively()
    {
        var rows = Instants
            .Select(at => new Row { Analysed = at, Mills = new DateTimeOffset(at).ToUnixTimeMilliseconds() })
            .AsQueryable();
        var (fromMills, toMills) = (new DateTimeOffset(From).ToUnixTimeMilliseconds(), new DateTimeOffset(To).ToUnixTimeMilliseconds());

        rows.InWindow(r => r.Analysed, new DateTimeOffset(From), new DateTimeOffset(To)).Should().HaveCount(3);
        rows.InWindow(r => r.Mills, fromMills, toMills).Should().HaveCount(3);
        rows.InWindow(r => r.Mills, null, toMills).Should().HaveCount(4);
        rows.InWindow(r => r.Mills, fromMills, null).Should().HaveCount(4);
    }

    [Fact]
    public void InWindow_ByASelector_IsTranslatedToSqlWithTheBoundsAsParameters()
    {
        using var db = OfflineDbContext.Create();

        var sql = db.StateSpans.InWindow(s => s.StartTimestamp, From, To).ToQueryString();

        var where = sql[sql.IndexOf("WHERE", StringComparison.Ordinal)..];
        where.Should().Contain("s.start_timestamp >= @from_Value AND s.start_timestamp <= @to_Value");
        where.Should().NotContain(From.Year.ToString());
    }

    [Fact]
    public void InHalfOpenWindow_IncludesTheLowerBoundAndExcludesTheUpper()
    {
        RowsAtEach().InHalfOpenWindow(r => r.Timestamp, From, To).Select(r => r.Timestamp)
            .Should().Equal(From, From.AddMinutes(30));
        RowsAtEach().InHalfOpenWindow(r => r.Timestamp, From, null).Should().HaveCount(4);
        RowsAtEach().InHalfOpenWindow(r => r.Timestamp, null, To).Should().HaveCount(3);
        RowsAtEach().InHalfOpenWindow(r => r.Timestamp, null, null).Should().HaveCount(Instants.Length);
    }

    [Fact]
    public void InHalfOpenWindow_SendsTheSameStatementForEveryWindowWithTheBoundsAsParameters()
    {
        using var db = OfflineDbContext.Create();

        var first = db.StateSpans.InHalfOpenWindow(s => s.StartTimestamp, From, To).ToQueryString();
        var second = db.StateSpans.InHalfOpenWindow(s => s.StartTimestamp, From.AddDays(3), To.AddDays(9)).ToQueryString();

        Statement(first).Should().Contain("s.start_timestamp >= @from_Value AND s.start_timestamp < @to_Value");
        Statement(first).Should().Be(Statement(second));
        Statement(first).Should().NotContain(From.Year.ToString());
    }

    private sealed class Span
    {
        public string Name { get; init; } = "";
        public DateTime Start { get; init; }
        public DateTime? End { get; init; }
        public DateTime Ended => End ?? DateTime.MaxValue;
    }

    private static Span Ended(string name, DateTime start, DateTime end) => new() { Name = name, Start = start, End = end };

    private static Span Open(string name, DateTime start) => new() { Name = name, Start = start };

    private static readonly Span[] Spans =
    [
        Ended("ended-before", From.AddHours(-2), From.AddMilliseconds(-1)),
        Ended("ends-at-from", From.AddHours(-2), From),
        Ended("inside", From.AddMinutes(10), From.AddMinutes(20)),
        Ended("starts-at-to", To, To.AddHours(1)),
        Ended("starts-after", To.AddMilliseconds(1), To.AddHours(1)),
        Open("open-before", From.AddHours(-3)),
        Open("open-inside", From.AddMinutes(5)),
        Open("open-after", To.AddHours(1)),
    ];

    private static IQueryable<Span> AllSpans() => Spans.AsQueryable();

    private static string[] Named(IQueryable<Span> spans) => [.. spans.Select(s => s.Name)];

    [Fact]
    public void OverlapsWindow_ByDefault_ReadsASpanTouchingEitherBoundAndAnOpenSpanThatStartedByTheWindowsEnd()
    {
        Named(AllSpans().OverlapsWindow(s => s.Start, s => s.End, From, To)).Should().Equal(
            "ends-at-from", "inside", "starts-at-to", "open-before", "open-inside");
    }

    [Fact]
    public void OverlapsWindow_StartAtToExcluded_DropsASpanStartingAtTheWindowsEnd()
    {
        Named(AllSpans().OverlapsWindow(s => s.Start, s => s.End, From, To, OverlapEdges.StartAtToExcluded)).Should().Equal(
            "ends-at-from", "inside", "open-before", "open-inside");
    }

    [Fact]
    public void OverlapsWindow_EndAtFromExcluded_DropsASpanEndingAtTheWindowsStart()
    {
        Named(AllSpans().OverlapsWindow(s => s.Start, s => s.End, From, To, OverlapEdges.EndAtFromExcluded)).Should().Equal(
            "inside", "starts-at-to", "open-before", "open-inside");
    }

    [Fact]
    public void OverlapsWindow_AlwaysReadingOpenSpans_KeepsAnOpenSpanThatStartedAfterTheWindow()
    {
        Named(AllSpans().OverlapsWindow(s => s.Start, s => s.End, From, To, open: OpenSpanRule.AlwaysRead)).Should().Equal(
            "ends-at-from", "inside", "starts-at-to", "open-before", "open-inside", "open-after");
    }

    [Fact]
    public void OverlapsWindow_ReadingOpenSpansOnlyWhenStartedInTheWindow_DropsAnOpenSpanThatStartedBeforeIt()
    {
        Named(AllSpans().OverlapsWindow(
                s => s.Start, s => s.End, From, null, open: OpenSpanRule.OnlyWhenStartedInWindow))
            .Should().Equal("ends-at-from", "inside", "starts-at-to", "starts-after", "open-inside", "open-after");
    }

    [Fact]
    public void OverlapsWindow_WithAnUpperBoundToo_ReadingOpenSpansOnlyWhenStartedInTheWindowStillStopsAtTheUpperBound()
    {
        Named(AllSpans().OverlapsWindow(
                s => s.Start, s => s.End, From, To, open: OpenSpanRule.OnlyWhenStartedInWindow))
            .Should().Equal("ends-at-from", "inside", "starts-at-to", "open-inside");
    }

    [Fact]
    public void OverlapsWindow_WithOneBoundOrNone_AppliesOnlyThatBound()
    {
        Named(AllSpans().OverlapsWindow(s => s.Start, s => s.End, From, null)).Should().Equal(
            "ends-at-from", "inside", "starts-at-to", "starts-after", "open-before", "open-inside", "open-after");
        Named(AllSpans().OverlapsWindow(s => s.Start, s => s.End, null, To)).Should().Equal(
            "ended-before", "ends-at-from", "inside", "starts-at-to", "open-before", "open-inside");
        AllSpans().OverlapsWindow(s => s.Start, s => s.End, null, null).Should().HaveCount(Spans.Length);
        AllSpans().OverlapsWindow(s => s.Start, s => s.End, null, null, open: OpenSpanRule.AlwaysRead)
            .Should().HaveCount(Spans.Length);
    }

    [Fact]
    public void OverlapsWindow_ForASpanThatCannotBeOpen_ReadsByTheSameBounds()
    {
        var ended = Spans.Where(s => s.End is not null).AsQueryable();

        Named(ended.OverlapsWindow(s => s.Start, s => s.Ended, From, To)).Should().Equal(
            "ends-at-from", "inside", "starts-at-to");
        Named(ended.OverlapsWindow(s => s.Start, s => s.Ended, From, To, OverlapEdges.StartAtToExcluded | OverlapEdges.EndAtFromExcluded))
            .Should().Equal("inside");
    }

    [Fact]
    public void OverlapsWindow_IsTranslatedToTheSameStatementForEveryWindowWithTheBoundsAsParameters()
    {
        using var db = OfflineDbContext.Create();

        string Sql(DateTime from, DateTime to, OverlapEdges edges, OpenSpanRule open) =>
            db.StateSpans.OverlapsWindow(s => s.StartTimestamp, s => s.EndTimestamp, from, to, edges, open).ToQueryString();

        foreach (var open in Enum.GetValues<OpenSpanRule>())
        {
            var first = Sql(From, To, OverlapEdges.StartAtToExcluded, open);
            var second = Sql(From.AddDays(3), To.AddDays(9), OverlapEdges.StartAtToExcluded, open);

            Statement(first).Should().Be(Statement(second));
            Statement(first).Should().NotContain(From.Year.ToString());
            Statement(first).Should().Contain("@from_Value").And.Contain("@to_Value");
        }

        db.CompressionLowSuggestions.OverlapsWindow(s => s.StartMills, s => s.EndMills, 10L, 20L, OverlapEdges.StartAtToExcluded)
            .ToQueryString().Should().Contain("start_mills < @to_Value").And.Contain("end_mills >= @from_Value");
    }

    private static string Statement(string sql) => sql[sql.IndexOf("SELECT", StringComparison.Ordinal)..];
}

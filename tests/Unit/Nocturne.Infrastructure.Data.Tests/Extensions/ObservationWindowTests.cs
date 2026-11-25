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
}

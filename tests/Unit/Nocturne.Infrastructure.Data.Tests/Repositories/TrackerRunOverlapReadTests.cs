using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

[Trait("Category", "Unit")]
public class TrackerRunOverlapReadTests
{
    private static readonly DateTime From = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = From.AddHours(1);

    private static TrackerInstanceEntity Run(string name, DateTime start, DateTime? completed) =>
        new() { StartNotes = name, StartedAt = start, CompletedAt = completed };

    [Fact]
    public void TheOverlapReadExcludesARunThatOnlyTouchesTheRangeEdges()
    {
        var runs = new[]
        {
            Run("ends-at-from", From.AddHours(-2), From),
            Run("starts-at-to", To, To.AddHours(1)),
            Run("inside", From.AddMinutes(10), From.AddMinutes(20)),
            Run("open-before", From.AddHours(-3), null),
            Run("open-at-to", To, null),
        }.AsQueryable();

        TrackerRepository.RunsOverlapping(runs, From, To).Select(r => r.StartNotes)
            .Should().Equal("inside", "open-before");
    }

    [Fact]
    public void TheOverlapReadSendsTheRangeAsParameters()
    {
        using var db = OfflineDbContext.Create();

        var first = TrackerRepository.RunsOverlapping(db.TrackerInstances, From, To).ToQueryString();
        var second = TrackerRepository.RunsOverlapping(db.TrackerInstances, From.AddDays(3), To.AddDays(9)).ToQueryString();

        Statement(first).Should().Be(Statement(second));
        Statement(first).Should().NotContain(From.Year.ToString());
        Statement(first).Should().Contain("@from_Value").And.Contain("@to_Value");
    }

    private static string Statement(string sql) => sql[sql.IndexOf("SELECT", StringComparison.Ordinal)..];
}

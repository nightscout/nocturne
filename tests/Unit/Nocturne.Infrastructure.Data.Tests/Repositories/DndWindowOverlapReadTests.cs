using Nocturne.Infrastructure.Data.Repositories;

namespace Nocturne.Infrastructure.Data.Tests.Repositories;

[Trait("Category", "Unit")]
public class DndWindowOverlapReadTests
{
    private static readonly DateTime From = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = From.AddHours(1);

    [Fact]
    public void TheOverlapReadSendsTheRangeAsParameters()
    {
        using var db = OfflineDbContext.Create();

        var first = DndWindowRepository.Overlapping(db.DndWindows, From, To).ToQueryString();
        var second = DndWindowRepository.Overlapping(db.DndWindows, From.AddDays(3), To.AddDays(9)).ToQueryString();

        Statement(first).Should().Be(Statement(second));
        Statement(first).Should().NotContain(From.Year.ToString());
        Statement(first).Should().Contain("@from_Value").And.Contain("@to_Value");
    }

    private static string Statement(string sql) => sql[sql.IndexOf("SELECT", StringComparison.Ordinal)..];
}

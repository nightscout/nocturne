using Nocturne.Infrastructure.Data.Extensions;

namespace Nocturne.Infrastructure.Data.Tests.Extensions;

public class MigrationCeilingsTests
{
    private const int PerCall = DatabaseInitializationExtensions.MigrationsPerCall;

    private static List<string> Pending(int count) =>
        Enumerable.Range(0, count).Select(i => $"{i:D14}_M").ToList();

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(PerCall)]
    public void At_most_one_call_of_pending_migrations_makes_a_single_uncapped_call(int count)
    {
        DatabaseInitializationExtensions.MigrationCeilings(Pending(count))
            .Should().Equal([null]);
    }

    [Fact]
    public void Each_call_but_the_last_stops_after_a_full_call_of_pending_migrations()
    {
        var pending = Pending(2 * PerCall + 1);

        DatabaseInitializationExtensions.MigrationCeilings(pending)
            .Should().Equal(pending[PerCall - 1], pending[2 * PerCall - 1], null);
    }

    [Fact]
    public void An_exact_multiple_ends_on_the_uncapped_call_instead_of_an_empty_one()
    {
        var pending = Pending(2 * PerCall);

        DatabaseInitializationExtensions.MigrationCeilings(pending)
            .Should().Equal(pending[PerCall - 1], null);
    }
}

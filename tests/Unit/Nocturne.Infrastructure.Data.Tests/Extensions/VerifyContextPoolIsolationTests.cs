using Nocturne.Infrastructure.Data.Extensions;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Extensions;

/// <summary>
/// The startup check that raw ADO.NET cannot reach the context pool, which skips the session reset
/// on pool return: a raw open there would read the previous lessee's RLS session state.
/// </summary>
[Trait("Category", "Unit")]
public class VerifyContextPoolIsolationTests
{
    private const string Base = "Host=localhost;Database=nocturne;Username=nocturne_app;Password=secret";

    private static NpgsqlDataSource Pool(string settings = "") => NpgsqlDataSource.Create(Base + settings);

    [Fact]
    public void AcceptsARawPoolThatKeepsTheReset()
    {
        using var contexts = Pool(";No Reset On Close=true");
        using var raw = Pool();

        var act = () => DatabaseInitializationExtensions.VerifyContextPoolIsolation(contexts, raw);

        act.Should().NotThrow();
    }

    [Fact]
    public void RejectsTheContextPoolRegisteredForRawOpens()
    {
        using var contexts = Pool(";No Reset On Close=true");

        var act = () => DatabaseInitializationExtensions.VerifyContextPoolIsolation(contexts, contexts);

        act.Should().Throw<InvalidOperationException>().WithMessage("*is the pool NocturneDbContext opens*");
    }

    [Theory]
    [InlineData(";No Reset On Close=true")]
    [InlineData(";Multiplexing=true")]
    public void RejectsARawPoolThatSkipsTheReset(string settings)
    {
        using var contexts = Pool(";No Reset On Close=true");
        using var raw = Pool(settings);

        var act = () => DatabaseInitializationExtensions.VerifyContextPoolIsolation(contexts, raw);

        act.Should().Throw<InvalidOperationException>().WithMessage("*does not reset a connection*");
    }
}

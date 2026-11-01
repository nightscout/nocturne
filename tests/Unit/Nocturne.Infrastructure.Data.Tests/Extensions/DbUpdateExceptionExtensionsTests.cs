using Microsoft.EntityFrameworkCore;
using Npgsql;
using Nocturne.Infrastructure.Data.Extensions;

namespace Nocturne.Infrastructure.Data.Tests.Extensions;

[Trait("Category", "Unit")]
public class DbUpdateExceptionExtensionsTests
{
    [Theory]
    [InlineData("22001", true)]
    [InlineData("22P05", true)]
    [InlineData("23502", true)]
    [InlineData("23505", true)]
    [InlineData("08006", false)]
    [InlineData("40001", false)]
    [InlineData("53300", false)]
    [InlineData("57014", false)]
    [InlineData("2", false)]
    public void Only_a_data_or_integrity_refusal_is_a_data_error(string sqlState, bool expected)
    {
        var ex = new DbUpdateException("refused", new PostgresException("refused", "ERROR", "ERROR", sqlState));

        ex.IsDataError().Should().Be(expected);
    }

    [Fact]
    public void A_failure_that_is_not_the_database_refusing_a_row_is_not_a_data_error()
    {
        new DbUpdateException("failed", new TimeoutException()).IsDataError().Should().BeFalse();
        new DbUpdateException("failed").IsDataError().Should().BeFalse();
    }
}

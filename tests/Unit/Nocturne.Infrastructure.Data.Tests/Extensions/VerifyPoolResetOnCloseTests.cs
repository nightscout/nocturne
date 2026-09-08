using Nocturne.Infrastructure.Data.Extensions;

namespace Nocturne.Infrastructure.Data.Tests.Extensions;

/// <summary>
/// The startup check that keeps <c>TenantConnectionInterceptor</c>'s empty close path safe: it
/// rejects the connection-string settings that stop Npgsql resetting a connection on pool return,
/// which is what clears the RLS GUCs between lessees.
/// </summary>
[Trait("Category", "Unit")]
public class VerifyPoolResetOnCloseTests
{
    private const string Base = "Host=localhost;Database=nocturne;Username=nocturne_app;Password=secret";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(Base)]
    [InlineData(Base + ";No Reset On Close=false")]
    [InlineData(Base + ";Multiplexing=false")]
    public void AcceptsAConnectionStringThatKeepsTheReset(string? connectionString)
    {
        var act = () => DatabaseInitializationExtensions.VerifyPoolResetOnClose(connectionString);

        act.Should().NotThrow();
    }

    [Fact]
    public void RejectsNoResetOnClose()
    {
        var act = () => DatabaseInitializationExtensions.VerifyPoolResetOnClose(
            Base + ";No Reset On Close=true");

        act.Should().Throw<InvalidOperationException>().WithMessage("*NoResetOnClose*");
    }

    [Fact]
    public void RejectsMultiplexing()
    {
        // Npgsql sends no reset for a multiplexed connection, so the GUCs would outlive the lessee
        // exactly as they would under NoResetOnClose.
        var act = () => DatabaseInitializationExtensions.VerifyPoolResetOnClose(
            Base + ";Multiplexing=true");

        act.Should().Throw<InvalidOperationException>().WithMessage("*Multiplexing*");
    }
}

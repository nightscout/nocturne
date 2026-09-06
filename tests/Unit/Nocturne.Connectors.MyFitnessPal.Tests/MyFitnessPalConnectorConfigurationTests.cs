using FluentAssertions;
using Nocturne.Connectors.MyFitnessPal.Configurations;
using Xunit;

namespace Nocturne.Connectors.MyFitnessPal.Tests;

public class MyFitnessPalConnectorConfigurationTests
{
    private static MyFitnessPalConnectorConfiguration Configured(
        string? password = null, string? refreshToken = null) => new()
        {
            Username = "someone",
            Password = password,
            RefreshToken = refreshToken,
        };

    [Fact]
    public void Validate_RejectsAConfigurationWithNoCredential()
    {
        var act = () => Configured().Validate();

        act.Should().Throw<ArgumentException>()
            .WithMessage("*Password or RefreshToken*");
    }

    [Theory]
    [InlineData("secret", null)]
    [InlineData(null, "refresh-token")]
    [InlineData("secret", "refresh-token")]
    public void Validate_AcceptsEitherCredential(string? password, string? refreshToken)
    {
        var act = () => Configured(password, refreshToken).Validate();

        act.Should().NotThrow();
    }
}

using Microsoft.Extensions.Configuration;
using Nocturne.Infrastructure.Data.Configuration;

namespace Nocturne.Infrastructure.Data.Tests.Configuration;

/// <summary>
/// Guards <see cref="PostgreSqlConfiguration.ResolveForEnvironment"/> — the assembly of the
/// options the API's own PostgreSQL registration is given. The registration itself sits behind
/// <c>if (!isTesting)</c> in <c>Program.cs</c> and every fixture forces the Testing environment,
/// so nothing that boots the host can observe it; these drive the resolution directly instead.
/// </summary>
[Trait("Category", "Unit")]
public class PostgreSqlConfigurationResolveTests
{
    private const string ConnectionString = "Host=localhost;Database=nocturne;Username=nocturne_app;Password=pw";

    private static IConfiguration SectionWith(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(
                s => $"{PostgreSqlConfiguration.SectionName}:{s.Key}",
                s => (string?)s.Value))
            .Build();

    [Fact]
    public void ConfiguredSection_ReachesEverySettingOnTheResolvedOptions()
    {
        var config = PostgreSqlConfiguration.ResolveForEnvironment(
            ConnectionString,
            SectionWith(
                ("MaxPoolSize", "33"),
                ("StatementTimeoutSeconds", "12"),
                ("CommandTimeoutSeconds", "45"),
                ("MaxRetryCount", "7"),
                ("MaxRetryDelaySeconds", "90")),
            isDevelopment: false);

        config.MaxPoolSize.Should().Be(33);
        config.StatementTimeoutSeconds.Should().Be(12);
        config.CommandTimeoutSeconds.Should().Be(45);
        config.MaxRetryCount.Should().Be(7);
        config.MaxRetryDelaySeconds.Should().Be(90);
        config.ConnectionString.Should().Be(ConnectionString);
    }

    /// <summary>
    /// The regression the extracted resolution exists to catch: dropping the configuration — the
    /// argument that was previously passed by a line no test could reach — has to change the
    /// resolved options, or nothing observes it being passed at all.
    /// </summary>
    [Fact]
    public void ConfiguredSection_ResolvesDifferentlyFromTheCompiledInDefaults()
    {
        var configured = PostgreSqlConfiguration.ResolveForEnvironment(
            ConnectionString,
            SectionWith(("MaxPoolSize", "33"), ("StatementTimeoutSeconds", "12")),
            isDevelopment: false);

        var defaults = new PostgreSqlConfiguration();

        configured.MaxPoolSize.Should().NotBe(defaults.MaxPoolSize);
        configured.StatementTimeoutSeconds.Should().NotBe(defaults.StatementTimeoutSeconds);
    }

    [Fact]
    public void EmptySection_KeepsTheDocumentedDefaults()
    {
        var config = PostgreSqlConfiguration.ResolveForEnvironment(
            ConnectionString,
            new ConfigurationBuilder().Build(),
            isDevelopment: false);

        var defaults = new PostgreSqlConfiguration();

        config.MaxPoolSize.Should().Be(defaults.MaxPoolSize);
        config.StatementTimeoutSeconds.Should().Be(defaults.StatementTimeoutSeconds);
        config.CommandTimeoutSeconds.Should().Be(defaults.CommandTimeoutSeconds);
        config.MaxRetryCount.Should().Be(defaults.MaxRetryCount);
        config.MaxRetryDelaySeconds.Should().Be(defaults.MaxRetryDelaySeconds);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EfDiagnostics_FollowTheEnvironment(bool isDevelopment)
    {
        var config = PostgreSqlConfiguration.ResolveForEnvironment(
            ConnectionString,
            new ConfigurationBuilder().Build(),
            isDevelopment);

        config.EnableDetailedErrors.Should().Be(isDevelopment);
        config.EnableSensitiveDataLogging.Should().Be(isDevelopment);
    }

    /// <summary>
    /// Both flags put query parameters — patient data — into logs and exception text, so a
    /// deployed appsettings or environment variable must not be able to turn them on outside
    /// development.
    /// </summary>
    [Fact]
    public void EfDiagnosticsInConfiguration_CannotOverrideTheEnvironment()
    {
        var config = PostgreSqlConfiguration.ResolveForEnvironment(
            ConnectionString,
            SectionWith(
                ("EnableSensitiveDataLogging", "true"),
                ("EnableDetailedErrors", "true")),
            isDevelopment: false);

        config.EnableSensitiveDataLogging.Should().BeFalse();
        config.EnableDetailedErrors.Should().BeFalse();
    }

    [Fact]
    public void MissingConfiguration_FailsRatherThanFallingBackToDefaults()
    {
        var resolve = () => PostgreSqlConfiguration.ResolveForEnvironment(
            ConnectionString, configuration: null!, isDevelopment: false);

        resolve.Should().Throw<ArgumentNullException>();
    }
}

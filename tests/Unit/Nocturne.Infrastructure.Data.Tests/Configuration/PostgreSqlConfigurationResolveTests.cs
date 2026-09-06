using Nocturne.Infrastructure.Data.Configuration;

namespace Nocturne.Infrastructure.Data.Tests.Configuration;

/// <summary>
/// Guards <see cref="PostgreSqlConfiguration.ResolveForEnvironment"/>, the assembly of the options
/// the API's own PostgreSQL registration is given. The registration itself sits behind
/// <c>if (!isTesting)</c> in <c>Program.cs</c> and every fixture forces the Testing environment, so
/// nothing that boots the host reaches it; these drive the resolution directly instead.
/// </summary>
[Trait("Category", "Unit")]
public class PostgreSqlConfigurationResolveTests
{
    [Fact]
    public void ConfiguredSection_ResolvesDifferentlyFromTheCompiledInDefaults()
    {
        var configured = PostgreSqlConfiguration.ResolveForEnvironment(
            PostgreSqlSection.AppConnectionString,
            PostgreSqlSection.With(("MaxPoolSize", "33"), ("StatementTimeoutSeconds", "12")),
            isDevelopment: false);

        var defaults = new PostgreSqlConfiguration();

        configured.MaxPoolSize.Should().NotBe(defaults.MaxPoolSize);
        configured.StatementTimeoutSeconds.Should().NotBe(defaults.StatementTimeoutSeconds);
    }

    /// <summary>
    /// <c>PostgreSql:ConnectionString</c> is a real key that the design-time factory reads, so a
    /// self-hoster running <c>dotnet ef</c> may have it pointed at the migrator role. The host's
    /// own connection string has to survive the bind, or the runtime pool connects as that role.
    /// </summary>
    [Fact]
    public void SuppliedConnectionString_SurvivesTheBind()
    {
        var config = PostgreSqlConfiguration.ResolveForEnvironment(
            PostgreSqlSection.AppConnectionString,
            PostgreSqlSection.With(
                ("ConnectionString", "Host=WRONGHOST;Database=nocturne;Username=nocturne_migrator;Password=pw")),
            isDevelopment: false);

        config.ConnectionString.Should().Be(PostgreSqlSection.AppConnectionString);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EfDiagnostics_FollowTheEnvironment(bool isDevelopment)
    {
        var config = PostgreSqlConfiguration.ResolveForEnvironment(
            PostgreSqlSection.AppConnectionString,
            PostgreSqlSection.With(),
            isDevelopment);

        config.EnableDetailedErrors.Should().Be(isDevelopment);
        config.EnableSensitiveDataLogging.Should().Be(isDevelopment);
    }

    /// <summary>
    /// Both flags put query parameters — patient data — into logs and exception text, so a deployed
    /// appsettings or environment variable must not be able to turn them on outside development.
    /// </summary>
    [Fact]
    public void EfDiagnosticsInConfiguration_CannotOverrideTheEnvironment()
    {
        var config = PostgreSqlConfiguration.ResolveForEnvironment(
            PostgreSqlSection.AppConnectionString,
            PostgreSqlSection.With(
                ("EnableSensitiveDataLogging", "true"),
                ("EnableDetailedErrors", "true")),
            isDevelopment: false);

        config.EnableSensitiveDataLogging.Should().BeFalse();
        config.EnableDetailedErrors.Should().BeFalse();
    }
}

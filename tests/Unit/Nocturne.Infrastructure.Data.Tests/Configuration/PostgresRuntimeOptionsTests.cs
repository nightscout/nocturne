using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Infrastructure.Data.Configuration;
using Nocturne.Infrastructure.Data.Extensions;
using Npgsql;

namespace Nocturne.Infrastructure.Data.Tests.Configuration;

/// <summary>
/// Guards that <see cref="PostgreSqlConfiguration"/> is reachable from configuration and that the
/// values on it reach the data source that owns the connection pool. These drive
/// <see cref="ServiceCollectionExtensions.AddPostgreSqlInfrastructure(IServiceCollection, string, IConfiguration, Action{PostgreSqlConfiguration})"/>
/// — the composition the API itself calls — rather than re-binding the section themselves.
/// </summary>
[Trait("Category", "Unit")]
public class PostgresRuntimeOptionsTests
{
    /// <summary>
    /// Reads the pool the registration actually built. The data source is registered as a
    /// singleton instance, so this needs no provider and touches nothing else in the graph.
    /// </summary>
    private static NpgsqlConnectionStringBuilder RegisteredPool(
        IConfiguration? configuration = null,
        Action<PostgreSqlConfiguration>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddPostgreSqlInfrastructure(PostgreSqlSection.AppConnectionString, configuration, configure);

        var dataSource = (NpgsqlDataSource)services
            .Single(d => d.ServiceType == typeof(NpgsqlDataSource))
            .ImplementationInstance!;

        using (dataSource)
        {
            return new NpgsqlConnectionStringBuilder(dataSource.ConnectionString);
        }
    }

    /// <summary>
    /// The API supplies the connection string separately from the section, so without the bind
    /// inside the registration every setting here would be unreachable in a deployed image and
    /// retuning the pool would need a rebuild.
    /// </summary>
    [Fact]
    public void ConfigurationSection_ReachesTheRuntimePool()
    {
        RegisteredPool(PostgreSqlSection.With(("MaxPoolSize", "33"))).MaxPoolSize.Should().Be(
            33,
            "an operator must be able to retune the pool without a code change and redeploy");
    }

    [Fact]
    public void ConfigurationSection_ReachesTheStatementTimeout()
    {
        RegisteredPool(PostgreSqlSection.With(("StatementTimeoutSeconds", "12"))).Options.Should().Contain(
            "statement_timeout=12000",
            "the server-side cap must be settable from configuration too");
    }

    /// <summary>
    /// Pins the order of the two writes to <c>ConnectionString</c>. The restore has to sit between
    /// the bind and the configure action: moved after it, the existing "cleared by the configure
    /// action" guard can never fire, because the restore silently repopulates what the action
    /// cleared.
    /// </summary>
    [Fact]
    public void ConfigureActionClearingTheConnectionString_StillFails()
    {
        var register = () => RegisteredPool(
            PostgreSqlSection.With(("MaxPoolSize", "33")),
            config => config.ConnectionString = string.Empty);

        register.Should().Throw<InvalidOperationException>()
            .WithMessage("*cleared by the configure action*");
    }

    [Fact]
    public void ConfigureAction_WinsOverTheSection()
    {
        var pool = RegisteredPool(
            PostgreSqlSection.With(("MaxPoolSize", "33")),
            config => config.MaxPoolSize = 44);

        pool.MaxPoolSize.Should().Be(
            44,
            "values the host derives at startup must override the file");
    }

    /// <summary>
    /// Asserted through the statement timeout rather than the pool size: the pool default and
    /// Npgsql's own default are both 100, so that comparison would hold even if the configuration
    /// never reached the data source.
    /// </summary>
    [Fact]
    public void WithoutConfiguration_TheDefaultsApply()
    {
        RegisteredPool().Options.Should().Contain(
            $"statement_timeout={new PostgreSqlConfiguration().StatementTimeoutSeconds * 1000}",
            "callers that pass no configuration must keep their existing behaviour");
    }

    /// <summary>
    /// <c>PostgreSql:ConnectionString</c> is a real key: the section documents it and the
    /// design-time factory reads it, so a self-hoster running <c>dotnet ef</c> may have it set to
    /// the migrator role. Binding must not let it displace the connection string the host resolved
    /// from <c>ConnectionStrings</c>, or the runtime pool silently connects as the wrong role.
    /// </summary>
    [Fact]
    public void SectionConnectionString_DoesNotDisplaceTheSuppliedOne()
    {
        var pool = RegisteredPool(PostgreSqlSection.With(
            ("MaxPoolSize", "33"),
            ("ConnectionString", "Host=WRONGHOST;Database=nocturne;Username=nocturne_migrator;Password=pw")));

        pool.Host.Should().Be("localhost", "the caller's connection string is the contract");
        pool.Username.Should().Be(
            "nocturne_app",
            "the runtime pool must never inherit the migrator role from configuration");
    }

    /// <summary>
    /// Binding makes a malformed value a startup failure where it was previously ignored, because
    /// nothing read the section at all. A wrong <em>key</em> is still silently inert — Bind ignores
    /// names it does not recognise — so this narrows the failure window rather than closing it.
    /// </summary>
    [Fact]
    public void MalformedValue_FailsAtStartup()
    {
        var register = () => RegisteredPool(PostgreSqlSection.With(("MaxPoolSize", "not-a-number")));

        register.Should().Throw<InvalidOperationException>();
    }

    /// <summary>
    /// The seconds-to-milliseconds scale is widened before multiplying. The dangerous window is
    /// not the negative wrap — PostgreSQL caps statement_timeout at 2,147,483,647 ms, so anything
    /// above 2,147,483 s is refused at connection open either way. It is the range that wraps back
    /// into a <em>valid</em> value: in int arithmetic 5,000,000 s scales to 705,032,704 ms, so a
    /// connection opens cleanly and silently reaps queries at eight days instead of eight weeks.
    /// </summary>
    [Fact]
    public void StatementTimeoutThatWouldWrapIntoAValidValue_KeepsItsMagnitude()
    {
        RegisteredPool(PostgreSqlSection.With(("StatementTimeoutSeconds", "5000000"))).Options
            .Should().Contain("statement_timeout=5000000000")
            .And.NotContain("statement_timeout=705032704");
    }
}

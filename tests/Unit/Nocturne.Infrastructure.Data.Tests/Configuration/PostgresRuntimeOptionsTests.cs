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
    /// Reads the context pool the registration actually built. Both pools are registered as
    /// singleton instances, so this needs no provider and touches nothing else in the graph.
    /// </summary>
    private static NpgsqlConnectionStringBuilder RegisteredPool(
        IConfiguration? configuration = null,
        Action<PostgreSqlConfiguration>? configure = null,
        string connectionString = PostgreSqlSection.AppConnectionString)
        => RegisteredPools(configuration, configure, connectionString).Contexts;

    private static (NpgsqlConnectionStringBuilder Contexts, NpgsqlConnectionStringBuilder Raw) RegisteredPools(
        IConfiguration? configuration = null,
        Action<PostgreSqlConfiguration>? configure = null,
        string connectionString = PostgreSqlSection.AppConnectionString)
    {
        var services = new ServiceCollection();
        services.AddPostgreSqlInfrastructure(connectionString, configuration, configure);

        using var contexts = ((ContextDataSource)services
            .Single(d => d.ServiceType == typeof(ContextDataSource))
            .ImplementationInstance!).Source;
        using var raw = (NpgsqlDataSource)services
            .Single(d => d.ServiceType == typeof(NpgsqlDataSource))
            .ImplementationInstance!;

        return (new NpgsqlConnectionStringBuilder(contexts.ConnectionString),
            new NpgsqlConnectionStringBuilder(raw.ConnectionString));
    }

    /// <summary>
    /// The context pool's session is overwritten on every open by <c>TenantConnectionInterceptor</c>;
    /// raw ADO.NET passes through no interceptor, so its pool must keep the reset.
    /// </summary>
    [Fact]
    public void ContextPool_SkipsTheSessionReset_AndTheRawPoolKeepsIt()
    {
        var (contexts, raw) = RegisteredPools(
            connectionString: PostgreSqlSection.AppConnectionString + ";No Reset On Close=true");

        contexts.NoResetOnClose.Should().BeTrue();
        raw.NoResetOnClose.Should().BeFalse("an operator's connection string must not turn the raw pool's reset off");
    }

    [Fact]
    public void RawPool_HoldsNoIdleConnections_AndSharesTheContextPoolsLimits()
    {
        var (contexts, raw) = RegisteredPools(PostgreSqlSection.With(
            ("MinPoolSize", "5"), ("MaxPoolSize", "33"), ("StatementTimeoutSeconds", "12")));

        contexts.MinPoolSize.Should().Be(5);
        raw.MinPoolSize.Should().Be(0, "raw ADO.NET opens are rare, so a floor there holds backends that do nothing");
        raw.MaxPoolSize.Should().Be(33);
        raw.Options.Should().Contain("statement_timeout=12000");
    }

    [Fact]
    public void Multiplexing_FailsAtStartup()
    {
        var register = () => RegisteredPool(connectionString: PostgreSqlSection.AppConnectionString + ";Multiplexing=true");

        register.Should().Throw<InvalidOperationException>().WithMessage("*Multiplexing*");
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
    public void RuntimePool_ClosesIdleConnectionsAfterAMinute_WithNoFloor()
    {
        var pool = RegisteredPool();

        pool.ConnectionIdleLifetime.Should().Be(60);
        pool.MinPoolSize.Should().Be(0);
    }

    [Fact]
    public void ConfigurationSection_ReachesThePoolFloorAndIdleLifetime()
    {
        var pool = RegisteredPool(PostgreSqlSection.With(
            ("MinPoolSize", "5"),
            ("ConnectionIdleLifetimeSeconds", "120")));

        pool.MinPoolSize.Should().Be(5);
        pool.ConnectionIdleLifetime.Should().Be(120);
    }

    [Theory]
    [InlineData("MinPoolSize", "-1")]
    [InlineData("MinPoolSize", "101")]
    [InlineData("ConnectionIdleLifetimeSeconds", "0")]
    [InlineData("ConnectionIdleLifetimeSeconds", "-5")]
    [InlineData("ConnectionIdleLifetimeSeconds", "5")]
    public void OutOfRangePoolSetting_FailsAtStartup(string key, string value)
    {
        var register = () => RegisteredPool(PostgreSqlSection.With((key, value)));

        register.Should().Throw<InvalidOperationException>().WithMessage($"*{key}*");
    }

    [Fact]
    public void RuntimePool_KeepsTheConnectionStringsOwnPoolFloorAndIdleLifetime()
    {
        var pool = RegisteredPool(
            PostgreSqlSection.With(("MinPoolSize", "5"), ("ConnectionIdleLifetimeSeconds", "120")),
            connectionString: PostgreSqlSection.AppConnectionString
                + ";Minimum Pool Size=7;Connection Idle Lifetime=300");

        pool.MinPoolSize.Should().Be(7);
        pool.ConnectionIdleLifetime.Should().Be(300);
    }

    [Fact]
    public void IdleLifetimeBelowTheConnectionStringsPruningInterval_FailsNamingTheKey()
    {
        var register = () => RegisteredPool(
            connectionString: PostgreSqlSection.AppConnectionString + ";Connection Pruning Interval=90");

        register.Should().Throw<InvalidOperationException>()
            .WithMessage("*PostgreSql:ConnectionIdleLifetimeSeconds*90*");
    }

    [Fact]
    public void MinPoolSizeAboveAConfiguredMaxPoolSize_FailsAtStartup()
    {
        var register = () => RegisteredPool(PostgreSqlSection.With(("MaxPoolSize", "10"), ("MinPoolSize", "11")));

        register.Should().Throw<InvalidOperationException>().WithMessage("*MinPoolSize*");
    }

    [Fact]
    public void RuntimePool_PreparesItsMostUsedStatements_AndConfigurationCanTurnItOff()
    {
        RegisteredPool().MaxAutoPrepare.Should().BePositive(
            "planning every execution afresh was over a third of the database's time under load");
        RegisteredPool(PostgreSqlSection.With(("MaxAutoPrepare", "0"))).MaxAutoPrepare.Should().Be(0);
    }

    [Fact]
    public void RuntimePool_KeepsTheConnectionStringsOwnMaxAutoPrepare()
    {
        RegisteredPool(connectionString: PostgreSqlSection.AppConnectionString + ";Max Auto Prepare=0")
            .MaxAutoPrepare.Should().Be(0, "an operator behind a transaction-mode pooler turns preparation off there");
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

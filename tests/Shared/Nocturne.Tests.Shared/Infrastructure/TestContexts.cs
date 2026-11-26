using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Configuration;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Interceptors;
using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace Nocturne.Tests.Shared.Infrastructure;

/// <summary>
/// Contexts and service providers over a test database, wired as the runtime wires them: the
/// <see cref="TenantConnectionInterceptor"/> on every connection, so a context reads and writes as the
/// role its connection string names. The builders differ in what they register, not in the role:
/// <see cref="MigrationsOptions(string)"/> adds the assembly that holds the migrations.
/// </summary>
public static class TestContexts
{
    public static readonly Action<NpgsqlDbContextOptionsBuilder> Retrying = o => o.EnableRetryOnFailure();

    /// <summary>Gives up within a few seconds, for a test that makes a write fail.</summary>
    public static readonly Action<NpgsqlDbContextOptionsBuilder> RetryingBriefly = o => o.EnableRetryOnFailure(
        maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(1), errorCodesToAdd: null);

    public static DbContextOptionsBuilder UseTenantInterceptor(
        this DbContextOptionsBuilder builder,
        string connectionString,
        IInterceptor[]? interceptors = null,
        Action<NpgsqlDbContextOptionsBuilder>? npgsql = null) =>
        builder.UseNpgsql(connectionString, npgsql).AddInterceptors(Interceptors(interceptors));

    public static DbContextOptionsBuilder UseTenantInterceptor(
        this DbContextOptionsBuilder builder,
        NpgsqlDataSource dataSource,
        IInterceptor[]? interceptors = null) =>
        builder.UseNpgsql(dataSource).AddInterceptors(Interceptors(interceptors));

    public static DbContextOptionsBuilder UseMigrations(this DbContextOptionsBuilder builder, string connectionString) =>
        builder.UseNpgsql(connectionString, npgsql => npgsql.UseNocturneMigrations())
            .AddInterceptors(new TenantConnectionInterceptor());

    /// <summary>For a test that reads the notices the migrations raise on a connection it holds.</summary>
    public static DbContextOptionsBuilder UseMigrations(this DbContextOptionsBuilder builder, NpgsqlConnection connection) =>
        builder.UseNpgsql(connection, npgsql => npgsql.UseNocturneMigrations())
            .AddInterceptors(new TenantConnectionInterceptor());

    public static DbContextOptions<NocturneDbContext> Options(
        string connectionString,
        IInterceptor[]? interceptors = null,
        Action<NpgsqlDbContextOptionsBuilder>? npgsql = null) =>
        Build(builder => builder.UseTenantInterceptor(connectionString, interceptors, npgsql));

    public static DbContextOptions<NocturneDbContext> Options(
        NpgsqlDataSource dataSource, IInterceptor[]? interceptors = null) =>
        Build(builder => builder.UseTenantInterceptor(dataSource, interceptors));

    public static DbContextOptions<NocturneDbContext> MigrationsOptions(string connectionString) =>
        Build(builder => builder.UseMigrations(connectionString));

    public static DbContextOptions<NocturneDbContext> MigrationsOptions(NpgsqlConnection connection) =>
        Build(builder => builder.UseMigrations(connection));

    /// <summary>Pinned to <paramref name="tenant"/> when one is given.</summary>
    public static NocturneDbContext Context(
        string connectionString,
        Guid? tenant = null,
        IInterceptor[]? interceptors = null,
        Action<NpgsqlDbContextOptionsBuilder>? npgsql = null) =>
        Pinned(new NocturneDbContext(Options(connectionString, interceptors, npgsql)), tenant);

    public static NocturneDbContext Context(
        NpgsqlDataSource dataSource, Guid? tenant = null, IInterceptor[]? interceptors = null) =>
        Pinned(new NocturneDbContext(Options(dataSource, interceptors)), tenant);

    /// <summary>
    /// Scope validation is on, as the API has it in development. The caller disposes the provider, which
    /// disposes the pools it built.
    /// </summary>
    /// <param name="postgres">Overrides for the runtime's pool and statement settings.</param>
    /// <param name="configure">Registrations the test adds or replaces.</param>
    public static ServiceProvider BuildProvider(
        string connectionString,
        Action<PostgreSqlConfiguration>? postgres = null,
        Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddPostgreSqlInfrastructure(connectionString, configuration: null, postgres);
        configure?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static DbContextOptions<NocturneDbContext> Build(Action<DbContextOptionsBuilder<NocturneDbContext>> use)
    {
        var builder = new DbContextOptionsBuilder<NocturneDbContext>();
        use(builder);
        return builder.Options;
    }

    private static IInterceptor[] Interceptors(IInterceptor[]? after) =>
        [new TenantConnectionInterceptor(), .. after ?? []];

    private static NocturneDbContext Pinned(NocturneDbContext context, Guid? tenant)
    {
        if (tenant is { } id)
            context.TenantId = id;
        return context;
    }
}

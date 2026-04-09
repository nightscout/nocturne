using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace Nocturne.Aspire.Hosting;

/// <summary>
/// Aspire helpers for wiring Nocturne's two-role PostgreSQL runtime.
/// Nocturne uses a non-privileged "nocturne_app" role at request time so that
/// Row Level Security actually enforces tenant isolation, and a privileged
/// "nocturne_migrator" role owns the schema and runs EF migrations.
///
/// Do NOT use <see cref="ResourceBuilderExtensions.WithReference{T}(IResourceBuilder{T}, IResourceBuilder{IResourceWithConnectionString}, string?, bool)"/>
/// to connect Nocturne services to Postgres — that auto-injects the bootstrap
/// superuser connection string, which defeats the role-separation model.
/// </summary>
public static class NocturneDatabaseExtensions
{
    public const string AppConnectionStringName = "nocturne-postgres";
    public const string MigratorConnectionStringName = "nocturne-postgres-migrator";

    /// <summary>
    /// Injects a NOCTURNE_POSTGRES_URI environment variable on the target
    /// resource using the nocturne_web PostgreSQL role. The SvelteKit web
    /// app's bot framework (@chat-adapter/state-pg) needs a postgresql://
    /// URL (it cannot parse the .NET key/value form) and connects as a
    /// least-privileged role that owns only its own chat_state_* tables.
    /// </summary>
    public static IResourceBuilder<T> WithNocturneWebDatabase<T>(
        this IResourceBuilder<T> resource,
        IResourceBuilder<PostgresServerResource> postgres,
        string databaseName,
        IResourceBuilder<ParameterResource> webPassword)
        where T : IResourceWithEnvironment
    {
        var endpoint = postgres.Resource.PrimaryEndpoint;

        var uri = ReferenceExpression.Create(
            $"postgresql://nocturne_web:{webPassword.Resource}@{endpoint.Property(EndpointProperty.Host)}:{endpoint.Property(EndpointProperty.Port)}/{databaseName}");

        return resource.WithEnvironment("NOCTURNE_POSTGRES_URI", uri);
    }

    /// <summary>
    /// Injects NOCTURNE_POSTGRES_URI for bring-your-own PostgreSQL setups.
    /// The operator provides a postgresql:// URL pointing at the
    /// nocturne_web role created by docs/postgres/bootstrap-roles.sql.
    /// </summary>
    public static IResourceBuilder<T> WithNocturneWebRemoteDatabase<T>(
        this IResourceBuilder<T> resource,
        string webUri)
        where T : IResourceWithEnvironment
    {
        return resource.WithEnvironment("NOCTURNE_POSTGRES_URI", webUri);
    }

    /// <summary>
    /// Injects two ConnectionStrings__ environment variables on the target
    /// resource, one per PostgreSQL role. Host and Port are pulled from the
    /// managed Postgres container's primary endpoint.
    /// </summary>
    public static IResourceBuilder<T> WithNocturneDatabase<T>(
        this IResourceBuilder<T> resource,
        IResourceBuilder<PostgresServerResource> postgres,
        string databaseName,
        IResourceBuilder<ParameterResource> appPassword,
        IResourceBuilder<ParameterResource> migratorPassword)
        where T : IResourceWithEnvironment
    {
        var endpoint = postgres.Resource.PrimaryEndpoint;

        var appConnectionString = ReferenceExpression.Create(
            $"Host={endpoint.Property(EndpointProperty.Host)};Port={endpoint.Property(EndpointProperty.Port)};Database={databaseName};Username=nocturne_app;Password={appPassword.Resource}");

        var migratorConnectionString = ReferenceExpression.Create(
            $"Host={endpoint.Property(EndpointProperty.Host)};Port={endpoint.Property(EndpointProperty.Port)};Database={databaseName};Username=nocturne_migrator;Password={migratorPassword.Resource}");

        return resource
            .WithEnvironment($"ConnectionStrings__{AppConnectionStringName}", appConnectionString)
            .WithEnvironment($"ConnectionStrings__{MigratorConnectionStringName}", migratorConnectionString);
    }

    /// <summary>
    /// Injects two ConnectionStrings__ environment variables on the target
    /// resource from the AppHost configuration. Used for bring-your-own
    /// PostgreSQL deployments. Throws if either string is missing.
    /// </summary>
    public static IResourceBuilder<T> WithNocturneRemoteDatabase<T>(
        this IResourceBuilder<T> resource,
        string appConnectionString,
        string migratorConnectionString)
        where T : IResourceWithEnvironment
    {
        return resource
            .WithEnvironment($"ConnectionStrings__{AppConnectionStringName}", appConnectionString)
            .WithEnvironment($"ConnectionStrings__{MigratorConnectionStringName}", migratorConnectionString);
    }
}

using Microsoft.Extensions.Configuration;

namespace Nocturne.Infrastructure.Data.Configuration;

/// <summary>
/// Configuration for PostgreSQL database connection
/// </summary>
public class PostgreSqlConfiguration
{
    /// <summary>
    /// Configuration section name
    /// </summary>
    public const string SectionName = "PostgreSql";

    /// <summary>
    /// Whether to use a remote database (true) or Aspire-managed local container (false)
    /// </summary>
    public bool UseRemoteDatabase { get; set; } = false;

    /// <summary>
    /// PostgreSQL connection string (optional - injected by Aspire when UseRemoteDatabase is false)
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Whether to enable sensitive data logging (for development only)
    /// </summary>
    public bool EnableSensitiveDataLogging { get; set; } = false;

    /// <summary>
    /// Whether to enable detailed errors (for development only)
    /// </summary>
    public bool EnableDetailedErrors { get; set; } = false;

    /// <summary>
    /// Maximum number of retry attempts for transient failures
    /// </summary>
    public int MaxRetryCount { get; set; } = 3;

    /// <summary>
    /// Maximum delay between retries in seconds
    /// </summary>
    public int MaxRetryDelaySeconds { get; set; } = 30;

    /// <summary>
    /// Command timeout in seconds
    /// </summary>
    public int CommandTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Server-side hard cap on how long any single statement may run, in seconds, applied as a
    /// PostgreSQL <c>statement_timeout</c> on the runtime connection pool. Backstops the
    /// client-side <see cref="CommandTimeoutSeconds"/>: a slow or expensive query is reaped by
    /// the server itself rather than merely abandoned client-side. Not applied to the migrator
    /// pool, whose DDL (e.g. a large index build) may legitimately run for minutes. A
    /// non-positive value leaves the server default (no cap).
    /// </summary>
    public int StatementTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Maximum number of physical connections in the Npgsql connection pool.
    /// Increase alongside Postgres max_connections when deploying at high concurrency.
    /// </summary>
    public int MaxPoolSize { get; set; } = 100;

    /// <summary>
    /// Resolves the options the runtime pool is built from: the compiled-in defaults on this type,
    /// with the <see cref="SectionName"/> section bound over them and <paramref name="configure"/>
    /// applied last, for values the host only knows at startup. A <see langword="null"/>
    /// <paramref name="configuration"/> is a decision to run on compiled-in defaults. An empty
    /// <paramref name="connectionString"/> passes through so a host that never opens the pool can
    /// still resolve its options; the registration that builds the pool is what rejects it.
    /// </summary>
    public static PostgreSqlConfiguration Resolve(
        string connectionString,
        IConfiguration? configuration,
        Action<PostgreSqlConfiguration>? configure = null)
    {
        var config = new PostgreSqlConfiguration { ConnectionString = connectionString };
        configuration?.GetSection(SectionName).Bind(config);

        // Restored after the bind, not before it. PostgreSql:ConnectionString is a documented key
        // that the design-time factory reads, so a self-hoster may well have it set; letting it
        // survive here would repoint the runtime pool at whatever role that key names.
        config.ConnectionString = connectionString;

        configure?.Invoke(config);

        if (!string.IsNullOrEmpty(connectionString) && string.IsNullOrEmpty(config.ConnectionString))
        {
            throw new InvalidOperationException(
                "Connection string was cleared by the configure action"
            );
        }

        return config;
    }

    /// <summary>
    /// Resolves the options the API itself runs on: <see cref="Resolve"/> plus the EF diagnostics
    /// the host derives from its environment. Both put query parameters — patient data — into logs
    /// and exception text, so they follow development and nothing a deployment can set.
    /// </summary>
    public static PostgreSqlConfiguration ResolveForEnvironment(
        string connectionString,
        IConfiguration configuration,
        bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return Resolve(connectionString, configuration, config =>
        {
            config.EnableDetailedErrors = isDevelopment;
            config.EnableSensitiveDataLogging = isDevelopment;
        });
    }
}

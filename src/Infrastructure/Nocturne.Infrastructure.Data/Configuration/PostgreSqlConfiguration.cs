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
    /// with <see cref="SectionName"/> bound over them and <paramref name="configure"/> applied last
    /// for values the host only knows at startup.
    /// </summary>
    /// <param name="connectionString">
    /// The connection string the host resolved. Always wins over the section's own
    /// <see cref="ConnectionString"/> key, which the design-time factory reads and a self-hoster
    /// may have pointed at the migrator role.
    /// </param>
    /// <param name="configuration">
    /// Configuration to bind the section from. <see langword="null"/> is a decision to run on
    /// compiled-in defaults.
    /// </param>
    /// <param name="configure">Overrides applied after the section.</param>
    public static PostgreSqlConfiguration Resolve(
        string connectionString,
        IConfiguration? configuration,
        Action<PostgreSqlConfiguration>? configure = null)
    {
        if (string.IsNullOrEmpty(connectionString))
        {
            throw new ArgumentException(
                "Connection string cannot be null or empty",
                nameof(connectionString)
            );
        }

        var config = new PostgreSqlConfiguration { ConnectionString = connectionString };
        configuration?.GetSection(SectionName).Bind(config);

        // Restored after the bind, not before it: see the connectionString parameter.
        config.ConnectionString = connectionString;

        configure?.Invoke(config);

        if (string.IsNullOrEmpty(config.ConnectionString))
        {
            throw new InvalidOperationException(
                "Connection string was cleared by the configure action"
            );
        }

        return config;
    }

    /// <summary>
    /// Resolves the options the API itself runs on: <see cref="Resolve"/> plus the EF diagnostics
    /// the host derives from its environment. Both leak query parameters — patient data — into
    /// logs and error text, so they follow development and nothing else.
    /// </summary>
    /// <param name="connectionString">The connection string the host resolved.</param>
    /// <param name="configuration">Configuration to bind <see cref="SectionName"/> from.</param>
    /// <param name="isDevelopment">Whether the host is running in the Development environment.</param>
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

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
}

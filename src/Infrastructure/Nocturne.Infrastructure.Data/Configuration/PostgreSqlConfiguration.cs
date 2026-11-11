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
    /// Physical connections the pool keeps open even when idle. Raise it to the steady load of a
    /// busy instance to skip reconnects; above that floor it holds backends that do nothing.
    /// Must not exceed <see cref="MaxPoolSize"/>. A <c>Minimum Pool Size</c> in the connection
    /// string wins over this.
    /// </summary>
    public int MinPoolSize { get; set; } = 0;

    /// <summary>
    /// How long, in seconds, a pooled connection above <see cref="MinPoolSize"/> may sit idle
    /// before the pool closes it. A shorter time returns backends sooner after a burst but costs a
    /// new connection (TLS and role setup) when load returns. Must be at least the pool's pruning
    /// interval (10 s by default), and is approximate: the pool closes surplus connections on that
    /// interval. A <c>Connection Idle Lifetime</c> in the connection string wins over this.
    /// </summary>
    public int ConnectionIdleLifetimeSeconds { get; set; } = 60;

    /// <summary>
    /// How many of its most-used statements each runtime-pool connection keeps prepared on the
    /// server (Npgsql <c>Max Auto Prepare</c>), so PostgreSQL reuses their plans instead of parsing
    /// and planning every execution; under load that work was over a third of the database's time.
    /// Each kept statement holds a plan in its backend's memory. Zero turns preparation off.
    /// </summary>
    /// <remarks>
    /// A <c>Max Auto Prepare</c> in the connection string wins over this. Behind a pooler in
    /// transaction mode that does not track prepared statements (PgBouncer before 1.21, or without
    /// <c>max_prepared_statements</c>), set either to zero: a prepared statement does not follow the
    /// client to the next server connection.
    /// </remarks>
    public int MaxAutoPrepare { get; set; } = 50;

    /// <summary>
    /// How long, in seconds, a runtime-pool transaction may sit idle between statements before
    /// PostgreSQL ends its session (<c>idle_in_transaction_session_timeout</c>). A non-positive
    /// value leaves the server default (no limit).
    /// </summary>
    public int IdleInTransactionTimeoutSeconds { get; set; } = 600;

    /// <summary>
    /// How far v3 history stays behind its newest deliverable stamp, in milliseconds; see
    /// <see cref="HistoryCommitMargin"/>.
    /// </summary>
    public int HistoryCommitMarginMilliseconds { get; set; } = (int)HistoryCommitMargin.Default.TotalMilliseconds;

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

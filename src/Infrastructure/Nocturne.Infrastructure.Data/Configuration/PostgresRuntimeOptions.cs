using Npgsql;

namespace Nocturne.Infrastructure.Data.Configuration;

/// <summary>
/// Runtime-pool connection tuning applied to the app <see cref="NpgsqlDataSource"/> at startup.
/// Kept separate from the migrator data source, which must remain uncapped.
/// </summary>
internal static class PostgresRuntimeOptions
{
    /// <summary>
    /// Appends a server-side <c>statement_timeout</c> to the connection's startup options so every
    /// statement on the runtime pool is cancelled by PostgreSQL itself once it exceeds the cap —
    /// the hard backstop behind the client-side command timeout. Applied only to the runtime app
    /// pool; the migrator pool is left uncapped because migration DDL (e.g. building an index on a
    /// multi-million-row table) may legitimately run for minutes. A non-positive value is a no-op,
    /// leaving the server default (0, meaning no cap).
    /// </summary>
    /// <param name="builder">Connection string builder for the runtime data source.</param>
    /// <param name="statementTimeoutSeconds">The cap in seconds; non-positive disables it.</param>
    internal static void ApplyStatementTimeout(
        NpgsqlConnectionStringBuilder builder,
        int statementTimeoutSeconds)
    {
        if (statementTimeoutSeconds <= 0)
        {
            return;
        }

        // statement_timeout is USERSET, so nocturne_app may set it via the startup 'options'
        // packet. Bare integer is milliseconds. Append rather than overwrite so any existing
        // startup options survive.
        var option = $"-c statement_timeout={statementTimeoutSeconds * 1000}";
        builder.Options = string.IsNullOrWhiteSpace(builder.Options)
            ? option
            : $"{builder.Options} {option}";
    }
}

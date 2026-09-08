using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Nocturne.Infrastructure.Data.Interceptors;

/// <summary>
/// EF Core connection interceptor that sets the PostgreSQL session variables
/// for Row-Level Security tenant isolation.
///
/// On connection open: SELECT set_config('app.current_tenant_id', $1, false)
///
/// Nothing is reset on close. Npgsql runs DISCARD ALL — which includes RESET ALL —
/// on every pool return, so each of these GUCs is already cleared before the next
/// lessee sees the physical connection, and
/// <c>DatabaseInitializationExtensions.VerifyNoResetOnClose</c> refuses to start the API
/// unless that is on. Issuing the RESETs here as well cost one extra round trip per
/// checkout and, at production checkout rates, well over half of the statements Postgres saw.
///
/// The same open path carries app.current_subject_id, which gives the
/// subject-scoped cross-tenant reads (tenant switcher, caregiver overview,
/// membership enumeration) reach over one subject's own rows. Both are set only
/// when non-empty, so an unpinned context leaves the GUC unset and matches nothing.
///
/// Additionally, on the first open against any given connection string, the
/// interceptor verifies that the connected role is neither a superuser nor
/// has BYPASSRLS. Both attributes silently defeat Row Level Security, so
/// Nocturne refuses to start when it detects them. The check result is
/// cached per connection string so it is paid exactly once per data source.
///
/// This class is safe to register as a singleton. All mutable state is
/// confined to a <see cref="ConcurrentDictionary{TKey, TValue}"/>.
/// </summary>
public class TenantConnectionInterceptor : DbConnectionInterceptor
{
    private readonly ConcurrentDictionary<string, bool> _verifiedRoles = new(StringComparer.Ordinal);

    /// <summary>
    /// Executes asynchronously when a connection is opened.
    /// Verifies the role attributes (once per connection string) and sets
    /// the PostgreSQL session variable for tenant isolation when a tenant
    /// is in scope.
    /// </summary>
    /// <param name="connection">The database connection.</param>
    /// <param name="eventData">Information about the connection event.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await EnsureRoleIsSafeAsync(connection, cancellationToken);

        if (eventData.Context is not NocturneDbContext ctx)
        {
            return;
        }

        await using var cmd = connection.CreateCommand();
        var clauses = new List<string>(5);

        if (ctx.TenantId != Guid.Empty)
        {
            clauses.Add("set_config('app.current_tenant_id', @tenant_id, false)");
            AddParameter(cmd, "tenant_id", ctx.TenantId.ToString());
        }

        if (ctx.SubjectId != Guid.Empty)
        {
            clauses.Add("set_config('app.current_subject_id', @subject_id, false)");
            AddParameter(cmd, "subject_id", ctx.SubjectId.ToString());
        }

        // app.is_share, app.visible_categories and app.share_full_history gate the
        // public-share RLS policies. All are set on every open so a pooled connection never
        // inherits a previous lessee's share state; for a share, a missing/empty
        // visible_categories denies all categorized data and a missing share_full_history
        // clamps reads to the last 24 hours (fail-closed).
        clauses.Add("set_config('app.is_share', @is_share, false)");
        AddParameter(cmd, "is_share", ctx.IsShareContext ? "true" : "false");

        clauses.Add("set_config('app.visible_categories', @visible_categories, false)");
        AddParameter(cmd, "visible_categories", ctx.VisibleCategories ?? string.Empty);

        clauses.Add("set_config('app.share_full_history', @share_full_history, false)");
        AddParameter(cmd, "share_full_history", ctx.ShareFullHistory ? "true" : "false");

        cmd.CommandText = "SELECT " + string.Join(", ", clauses);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddParameter(DbCommand cmd, string name, string value)
    {
        var param = cmd.CreateParameter();
        param.ParameterName = name;
        param.Value = value;
        cmd.Parameters.Add(param);
    }

    private async Task EnsureRoleIsSafeAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var key = connection.ConnectionString ?? string.Empty;
        if (_verifiedRoles.ContainsKey(key))
        {
            return;
        }

        // If the check itself fails (network, permission denied on pg_roles, etc.)
        // we let the exception propagate. Do NOT cache and do NOT silently proceed.
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT current_user, rolsuper, rolbypassrls FROM pg_roles WHERE rolname = current_user";

        string user;
        bool isSuper;
        bool bypassRls;
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException(
                    "Role attribute self-check could not determine the current database user.");
            }

            user = reader.GetString(0);
            isSuper = reader.GetBoolean(1);
            bypassRls = reader.GetBoolean(2);
        }

        if (isSuper || bypassRls)
        {
            throw new InvalidOperationException(
                $"Database role '{user}' bypasses Row Level Security (superuser={isSuper}, bypassrls={bypassRls}). " +
                "Nocturne refuses to start with this role. Use 'nocturne_app' for the runtime connection string.");
        }

        _verifiedRoles.TryAdd(key, true);
    }
}

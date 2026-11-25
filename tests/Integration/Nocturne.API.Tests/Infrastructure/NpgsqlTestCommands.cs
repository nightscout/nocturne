using Npgsql;

namespace Nocturne.API.Tests.Integration.Infrastructure;

/// <summary>
/// Raw SQL on an open test connection, with named parameters bound from tuples.
/// </summary>
public static class NpgsqlTestCommands
{
    public static async Task<int> ExecuteAsync(
        this NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters)
    {
        await using var cmd = Command(conn, sql, parameters);
        return await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Runs <paramref name="sql"/> and returns the first column of its first row. A missing row or a
    /// null column is <c>default</c> for a reference or nullable <typeparamref name="T"/> and a failure
    /// for any other, so a count or an id the query should have found cannot read as zero or empty.
    /// </summary>
    public static async Task<T> ScalarAsync<T>(
        this NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters)
    {
        await using var cmd = Command(conn, sql, parameters);
        var result = await cmd.ExecuteScalarAsync();
        if (result is null or DBNull)
        {
            return default(T) is null
                ? default!
                : throw new InvalidOperationException($"The query returned no value for {typeof(T).Name}: {sql}");
        }

        return (T)result;
    }

    /// <summary>
    /// Maps the rows of <paramref name="sql"/>. A leading <c>set_config</c> statement is run and its
    /// result left unread.
    /// </summary>
    public static async Task<List<T>> QueryAsync<T>(
        this NpgsqlConnection conn, string sql, Func<NpgsqlDataReader, T> read,
        params (string Name, object Value)[] parameters)
    {
        await using var cmd = Command(conn, sql, parameters);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (reader.FieldCount > 0 && reader.GetName(0) == "set_config")
            await reader.NextResultAsync();

        var rows = new List<T>();
        while (await reader.ReadAsync())
            rows.Add(read(reader));
        return rows;
    }

    /// <summary>
    /// <see cref="ScalarAsync{T}"/> under the seeded tenant, with the one parameter <c>@key</c>. The tenant
    /// is pinned for the rest of the connection's life, not only for this call.
    /// </summary>
    public static async Task<T> ScalarInTenantAsync<T>(this NpgsqlConnection conn, string sql, object key)
    {
        var tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        await conn.ExecuteAsync(
            "SELECT set_config('app.current_tenant_id', @tenantId, false);", ("tenantId", tenantId.ToString()));
        return await conn.ScalarAsync<T>(sql, ("key", key));
    }

    private static NpgsqlCommand Command(
        NpgsqlConnection conn, string sql, (string Name, object Value)[] parameters)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        return cmd;
    }
}

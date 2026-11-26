using Npgsql;

namespace Nocturne.Tests.Shared.Infrastructure;

/// <summary>
/// Raw SQL on a test database, with named parameters bound from tuples. Every form runs on a connection;
/// the data source and connection string forms open one for the call and dispose it.
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
    /// Sets <c>app.current_tenant_id</c> for the rest of the connection's life, as the runtime does when it
    /// opens a connection, so a connection that bypasses the context filter reads and writes as that tenant.
    /// With <paramref name="local"/> the pin ends with the open transaction, and outside one it ends at once.
    /// </summary>
    public static Task PinTenantAsync(this NpgsqlConnection conn, Guid tenantId, bool local = false) =>
        conn.ExecuteAsync(
            "SELECT set_config('app.current_tenant_id', @tenantId, @local);",
            ("tenantId", tenantId.ToString()), ("local", local));

    public static async Task<int> ExecuteAsync(
        this NpgsqlDataSource dataSource, string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        return await conn.ExecuteAsync(sql, parameters);
    }

    public static async Task<T> ScalarAsync<T>(
        this NpgsqlDataSource dataSource, string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        return await conn.ScalarAsync<T>(sql, parameters);
    }

    public static async Task<List<T>> QueryAsync<T>(
        this NpgsqlDataSource dataSource, string sql, Func<NpgsqlDataReader, T> read,
        params (string Name, object Value)[] parameters)
    {
        await using var conn = await dataSource.OpenConnectionAsync();
        return await conn.QueryAsync(sql, read, parameters);
    }

    public static async Task<int> ExecuteAsync(
        string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await OpenAsync(connectionString);
        return await conn.ExecuteAsync(sql, parameters);
    }

    public static async Task<T> ScalarAsync<T>(
        string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await OpenAsync(connectionString);
        return await conn.ScalarAsync<T>(sql, parameters);
    }

    public static async Task<List<T>> QueryAsync<T>(
        string connectionString, string sql, Func<NpgsqlDataReader, T> read,
        params (string Name, object Value)[] parameters)
    {
        await using var conn = await OpenAsync(connectionString);
        return await conn.QueryAsync(sql, read, parameters);
    }

    public static async Task<NpgsqlConnection> OpenAsync(string connectionString)
    {
        var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        return conn;
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

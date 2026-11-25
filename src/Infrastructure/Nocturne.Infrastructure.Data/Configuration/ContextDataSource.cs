using Npgsql;

namespace Nocturne.Infrastructure.Data.Configuration;

/// <summary>
/// The pool every <see cref="NocturneDbContext"/> opens (<see cref="PostgresRuntimeOptions.BuildRuntimeDataSource"/>).
/// It is held here rather than registered as <see cref="NpgsqlDataSource"/>, which resolves to the raw
/// pool, so code outside Infrastructure cannot open it. Disposing it closes the pool.
/// </summary>
internal sealed record ContextDataSource(NpgsqlDataSource Source) : IAsyncDisposable, IDisposable
{
    public ValueTask DisposeAsync() => Source.DisposeAsync();

    public void Dispose() => Source.Dispose();
}

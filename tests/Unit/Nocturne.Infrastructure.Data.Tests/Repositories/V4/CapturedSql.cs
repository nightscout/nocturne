using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Nocturne.Infrastructure.Data.Tests.Repositories.V4;

/// <summary>
/// A <see cref="NocturneDbContext"/> on the Npgsql provider that opens no connection and answers
/// every query with no rows, keeping the SQL each one sent.
/// </summary>
internal sealed class CapturedSql : DbCommandInterceptor, IDbConnectionInterceptor
{
    private readonly List<string> _commands = [];

    public IReadOnlyList<string> Commands => _commands;

    public NocturneDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql("Host=localhost;Database=nocturne;Username=test;Password=test")
            .AddInterceptors(this)
            .Options)
        { TenantId = Guid.NewGuid() };

    public ValueTask<InterceptionResult> ConnectionOpeningAsync(
        DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(InterceptionResult.Suppress());

    public InterceptionResult ConnectionOpening(
        DbConnection connection, ConnectionEventData eventData, InterceptionResult result) =>
        InterceptionResult.Suppress();

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        _commands.Add(command.CommandText);
        return ValueTask.FromResult(InterceptionResult<DbDataReader>.SuppressWithResult(new DataTable().CreateDataReader()));
    }
}

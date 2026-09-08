using System.Data;
using System.Data.Common;
using Nocturne.Infrastructure.Data.Interceptors;

namespace Nocturne.Infrastructure.Data.Tests.Interceptors;

/// <summary>
/// Pins the close path of <see cref="TenantConnectionInterceptor"/> as free of round trips.
/// Npgsql's DISCARD ALL on pool return clears every RLS GUC the interceptor sets, so a reset
/// here is one wasted statement per checkout — 87 % of everything production Postgres saw.
/// <c>PoolReturnDiscardsRlsGucsIntegrationTests</c> is the proof that DISCARD ALL suffices.
/// </summary>
[Trait("Category", "Unit")]
public class TenantConnectionInterceptorTests
{
    [Fact]
    public async Task ConnectionClosingAsync_IssuesNoCommand()
    {
        var interceptor = new TenantConnectionInterceptor();
        await using var connection = new CommandRefusingConnection();

        // The event data goes unread by a close that touches nothing.
        var result = await interceptor.ConnectionClosingAsync(connection, null!, default);

        result.IsSuppressed.Should().BeFalse();
    }

    [Fact]
    public void ConnectionClosing_IssuesNoCommand()
    {
        var interceptor = new TenantConnectionInterceptor();
        using var connection = new CommandRefusingConnection();

        var result = interceptor.ConnectionClosing(connection, null!, default);

        result.IsSuppressed.Should().BeFalse();
    }

    private sealed class CommandRefusingConnection : DbConnection
    {
        public override string ConnectionString { get; set; } = string.Empty;

        public override string Database => string.Empty;

        public override string DataSource => string.Empty;

        public override string ServerVersion => string.Empty;

        public override ConnectionState State => ConnectionState.Open;

        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

        public override void Close()
        {
        }

        public override void Open() => throw new NotSupportedException();

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
            => throw new NotSupportedException();

        protected override DbCommand CreateDbCommand()
            => throw new InvalidOperationException(
                "Closing a connection must issue no command: the pool's DISCARD ALL already clears the RLS GUCs.");
    }
}

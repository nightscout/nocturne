using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Nocturne.Infrastructure.Data.Interceptors;

namespace Nocturne.Infrastructure.Data.Tests.Interceptors;

/// <summary>
/// Pins the close path of <see cref="TenantConnectionInterceptor"/> as free of round trips. The next
/// open overwrites every RLS GUC the interceptor sets, so a command issued here buys nothing;
/// <c>PooledLeaseSessionIsolationTests</c> is the proof, and of a failed open closing the connection.
/// </summary>
[Trait("Category", "Unit")]
public class TenantConnectionInterceptorTests
{
    [Fact]
    public async Task ConnectionClosingAsync_IssuesNoCommand()
    {
        var interceptor = new TenantConnectionInterceptor();
        await using var connection = new CommandCountingConnection();

        // The event data goes unread by a close that touches nothing.
        var result = await interceptor.ConnectionClosingAsync(connection, null!, default);

        connection.CommandsCreated.Should().Be(0);
        result.IsSuppressed.Should().BeFalse();
    }

    [Fact]
    public void ConnectionClosing_IssuesNoCommand()
    {
        var interceptor = new TenantConnectionInterceptor();
        using var connection = new CommandCountingConnection();

        var result = interceptor.ConnectionClosing(connection, null!, default);

        connection.CommandsCreated.Should().Be(0);
        result.IsSuppressed.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFailedRoleCheck_ClosesTheConnection(bool async)
    {
        var interceptor = new TenantConnectionInterceptor();
        await using var connection = new CommandCountingConnection();

        // The role check runs before the event data is read, and this connection's reader throws.
        Func<Task> open = async
            ? () => interceptor.ConnectionOpenedAsync(connection, null!, default)
            : () =>
            {
                interceptor.ConnectionOpened(connection, null!);
                return Task.CompletedTask;
            };

        await open.Should().ThrowAsync<NotSupportedException>();
        connection.Closes.Should().Be(1);
    }

    /// <summary>
    /// Counts rather than throws: a close that creates a command and swallows the failure would
    /// still look clean to an assertion that relies on the exception escaping.
    /// </summary>
    private sealed class CommandCountingConnection : DbConnection
    {
        public int CommandsCreated { get; private set; }

        public int Closes { get; private set; }

        [AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;

        public override string Database => string.Empty;

        public override string DataSource => string.Empty;

        public override string ServerVersion => string.Empty;

        public override ConnectionState State => ConnectionState.Open;

        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

        public override void Close() => Closes++;

        public override void Open() => throw new NotSupportedException();

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
            => throw new NotSupportedException();

        protected override DbCommand CreateDbCommand()
        {
            CommandsCreated++;
            return new NoOpCommand();
        }
    }

    private sealed class NoOpCommand : DbCommand
    {
        [AllowNull]
        public override string CommandText { get; set; } = string.Empty;

        public override int CommandTimeout { get; set; }

        public override CommandType CommandType { get; set; }

        public override bool DesignTimeVisible { get; set; }

        public override UpdateRowSource UpdatedRowSource { get; set; }

        protected override DbConnection? DbConnection { get; set; }

        protected override DbParameterCollection DbParameterCollection { get; } = new NoOpParameterCollection();

        protected override DbTransaction? DbTransaction { get; set; }

        public override void Cancel()
        {
        }

        public override int ExecuteNonQuery() => 0;

        public override object? ExecuteScalar() => null;

        public override void Prepare()
        {
        }

        protected override DbParameter CreateDbParameter() => throw new NotSupportedException();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
            => throw new NotSupportedException();
    }

    private sealed class NoOpParameterCollection : DbParameterCollection
    {
        private readonly List<object> _items = [];

        public override int Count => _items.Count;

        public override object SyncRoot => _items;

        public override int Add(object value)
        {
            _items.Add(value);
            return _items.Count - 1;
        }

        public override void AddRange(Array values) => throw new NotSupportedException();

        public override void Clear() => _items.Clear();

        public override bool Contains(object value) => _items.Contains(value);

        public override bool Contains(string value) => false;

        public override void CopyTo(Array array, int index) => throw new NotSupportedException();

        public override System.Collections.IEnumerator GetEnumerator() => _items.GetEnumerator();

        public override int IndexOf(object value) => _items.IndexOf(value);

        public override int IndexOf(string parameterName) => -1;

        public override void Insert(int index, object value) => _items.Insert(index, value);

        public override void Remove(object value) => _items.Remove(value);

        public override void RemoveAt(int index) => _items.RemoveAt(index);

        public override void RemoveAt(string parameterName) => throw new NotSupportedException();

        protected override DbParameter GetParameter(int index) => throw new NotSupportedException();

        protected override DbParameter GetParameter(string parameterName) => throw new NotSupportedException();

        protected override void SetParameter(int index, DbParameter value) => throw new NotSupportedException();

        protected override void SetParameter(string parameterName, DbParameter value)
            => throw new NotSupportedException();
    }
}

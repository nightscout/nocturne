using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Nocturne.Tests.Shared.Infrastructure;

/// <summary>
/// Records every statement a context runs, so a test can pin a round-trip count or a statement's
/// shape, which row counts cannot show. Statements are stored with leading whitespace trimmed.
/// </summary>
public sealed class StatementRecorder : DbCommandInterceptor
{
    private readonly object _gate = new();
    private readonly List<string> _statements = [];
    private Func<string, bool>? _failOn;
    private int _failOnOrdinal;
    private int _failOnMatches;

    public IReadOnlyList<string> All
    {
        get { lock (_gate) return _statements.ToList(); }
    }

    public void Clear()
    {
        lock (_gate) _statements.Clear();
    }

    public int Count(string keyword) => Count(keyword, _ => true);

    public int Count(string keyword, Func<string, bool> where) =>
        All.Count(s => s.StartsWith(keyword, StringComparison.OrdinalIgnoreCase) && where(s));

    /// <summary>Throws as the <paramref name="ordinal"/>th statement matching <paramref name="match"/> is about to run.</summary>
    public void FailOn(Func<string, bool> match, int ordinal = 1)
    {
        lock (_gate)
        {
            _failOn = match;
            _failOnOrdinal = ordinal;
            _failOnMatches = 0;
        }
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Record(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Record(command);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Record(command);
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return ValueTask.FromResult(result);
    }

    private void Record(DbCommand command)
    {
        var text = command.CommandText.TrimStart();
        bool fail;
        lock (_gate)
        {
            _statements.Add(text);
            fail = _failOn is { } match && match(text) && ++_failOnMatches == _failOnOrdinal;
        }

        if (fail)
            throw new InvalidOperationException("injected write failure");
    }
}

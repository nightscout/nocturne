using Microsoft.Extensions.Logging;

namespace Nocturne.Tests.Shared.Logging;

/// <summary>A logger that keeps everything written to it, safe to write from many threads.</summary>
public class ListLogger : ILogger
{
    private readonly object _gate = new();
    private readonly List<string> _scopes = [];

    public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

    /// <summary>The structured properties of each entry, at the same index as <see cref="Entries"/>.</summary>
    public List<IReadOnlyDictionary<string, object?>> Properties { get; } = new();

    public IEnumerable<string> Warnings => MessagesAt(level => level == LogLevel.Warning);

    /// <summary>The messages logged at error or above.</summary>
    public IReadOnlyList<string> Errors => MessagesAt(level => level >= LogLevel.Error);

    public IReadOnlyList<string> Messages => MessagesAt(_ => true);

    public IEnumerable<(string Message, IReadOnlyDictionary<string, object?> Properties)> WarningEvents
    {
        get
        {
            lock (_gate)
                return Entries.Zip(Properties)
                    .Where(entry => entry.First.Level == LogLevel.Warning)
                    .Select(entry => (entry.First.Message, entry.Second))
                    .ToList();
        }
    }

    /// <summary>
    /// Every string the logger was given: each message, its exception, each property value and each
    /// scope. For asserting that nothing logged carries a secret.
    /// </summary>
    public IReadOnlyList<string> Text
    {
        get
        {
            lock (_gate)
                return Entries.Zip(Properties)
                    .SelectMany(entry => new[] { entry.First.Message, entry.First.Exception?.ToString() }
                        .Concat(entry.Second.Values.Select(value => value?.ToString())))
                    .Concat(_scopes)
                    .OfType<string>()
                    .ToList();
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        lock (_gate)
            _scopes.Add(state.ToString() ?? string.Empty);
        return null;
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var properties = new Dictionary<string, object?>();
        if (state is IEnumerable<KeyValuePair<string, object?>> values)
        {
            foreach (var (key, value) in values)
                properties[key] = value;
        }

        var message = formatter(state, exception);
        lock (_gate)
        {
            Entries.Add((logLevel, message, exception));
            Properties.Add(properties);
        }
    }

    private List<string> MessagesAt(Func<LogLevel, bool> level)
    {
        lock (_gate)
            return Entries.Where(e => level(e.Level)).Select(e => e.Message).ToList();
    }
}

/// <inheritdoc cref="ListLogger"/>
public sealed class ListLogger<T> : ListLogger, ILogger<T>;

/// <summary>Hands every category the one <see cref="Logger"/>, so a test reads what a whole service graph logged.</summary>
public sealed class ListLoggerProvider : ILoggerProvider
{
    public ListLogger Logger { get; } = new();

    public ILogger CreateLogger(string categoryName) => Logger;

    public void Dispose()
    {
    }
}

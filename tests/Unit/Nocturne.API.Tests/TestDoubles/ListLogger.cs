using Microsoft.Extensions.Logging;

namespace Nocturne.API.Tests.TestDoubles;

/// <summary>Minimal capturing logger for asserting structured log events.</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

    /// <summary>The structured properties of each entry, at the same index as <see cref="Entries"/>.</summary>
    public List<IReadOnlyDictionary<string, object?>> Properties { get; } = new();

    public IEnumerable<string> Warnings =>
        Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message);

    public IEnumerable<(string Message, IReadOnlyDictionary<string, object?> Properties)> WarningEvents =>
        Entries.Zip(Properties)
            .Where(entry => entry.First.Level == LogLevel.Warning)
            .Select(entry => (entry.First.Message, entry.Second));

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Entries.Add((logLevel, formatter(state, exception), exception));
        Properties.Add(state is IEnumerable<KeyValuePair<string, object?>> values
            ? values.ToDictionary(value => value.Key, value => value.Value)
            : new Dictionary<string, object?>());
    }
}

using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Extensions;
using Xunit;

namespace Nocturne.Connectors.Core.Tests.Extensions;

/// <summary>
///     A connector installs, and shows in the UI, only if a scan reaches its types. One type that
///     cannot be loaded fails <see cref="Assembly.GetTypes"/> for its whole assembly, so a scan that
///     reads the failure as "no types here" drops every connector the assembly ships.
/// </summary>
public class ConnectorAssemblyScanTests
{
    [Fact]
    public void AnAssemblyThatLoadsInPart_StillContributesItsLoadableTypes()
    {
        var partialLoad = new ReflectionTypeLoadException(
            [typeof(string), null, typeof(int)],
            [null, new TypeLoadException("Nocturne.Missing, Version=2.0.0 not found"), null]);

        partialLoad.LoadableTypes("Nocturne.Connectors.Broken")
            .Should().Equal(typeof(string), typeof(int));
    }

    [Fact]
    public void AnAssemblyThatLoadsInPart_ReportsTheLoaderFailures()
    {
        var logger = new RecordingLogger();
        var partialLoad = new ReflectionTypeLoadException(
            [null],
            [new TypeLoadException("Nocturne.Missing, Version=2.0.0 not found")]);

        _ = partialLoad.LoadableTypes("Nocturne.Connectors.Broken", logger).ToList();

        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Message.Should().Contain("Nocturne.Connectors.Broken")
            .And.Contain("Nocturne.Missing, Version=2.0.0 not found");
    }

    [Fact]
    public void AnAssemblyThatLoadsWhole_IsScannedWithoutComment()
    {
        var logger = new RecordingLogger();

        typeof(ConnectorAssemblyScanTests).Assembly.LoadableTypes(logger)
            .Should().Contain(typeof(ConnectorAssemblyScanTests));

        logger.Entries.Should().BeEmpty();
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}

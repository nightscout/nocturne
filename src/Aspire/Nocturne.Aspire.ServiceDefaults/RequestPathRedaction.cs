using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Rewrites a request path whose segments carry a credential, for a service whose routes take one.
/// </summary>
/// <remarks>
/// It recognises only a path starting with <c>/</c>, optionally followed by a query string. The
/// telemetry appliers in this file also redact the path of an absolute URL (see
/// <see cref="RequestPathRedactionExtensions.TryRedactValue"/>). Code that composes anything else
/// around a path, such as <c>"GET /api/..."</c>, redacts the path with
/// <see cref="RequestPathRedactionExtensions.Redact"/> before composing it.
/// </remarks>
public interface IRequestPathRedactor
{
    /// <summary>
    /// Returns false, leaving <paramref name="redacted"/> null, when <paramref name="path"/> carries
    /// no credential.
    /// </summary>
    bool TryRedact(string path, [NotNullWhen(true)] out string? redacted);
}

public static class RequestPathRedactionExtensions
{
    /// <summary>
    /// Registers <typeparamref name="TRedactor"/> and the log scope provider that applies it.
    /// The log and span processors registered in <see cref="Extensions"/> pick it up from DI.
    /// </summary>
    public static IServiceCollection AddRequestPathRedactor<TRedactor>(this IServiceCollection services)
        where TRedactor : class, IRequestPathRedactor
    {
        services.AddSingleton<TRedactor>();
        services.AddSingleton<IRequestPathRedactor>(sp => sp.GetRequiredService<TRedactor>());
        services.AddSingleton<IExternalScopeProvider>(sp =>
            new RequestPathRedactingScopeProvider(sp.GetRequiredService<IRequestPathRedactor>())
        );
        return services;
    }

    /// <summary>Returns <paramref name="path"/> with its credential segments redacted.</summary>
    public static string Redact(this IRequestPathRedactor redactor, string path) =>
        redactor.TryRedact(path, out var redacted) ? redacted : path;

    /// <summary>
    /// Redacts a telemetry value that is a path or an absolute URL, whose path is redacted in place.
    /// </summary>
    internal static bool TryRedactValue(
        this IRequestPathRedactor redactor,
        string value,
        [NotNullWhen(true)] out string? redacted
    )
    {
        if (value.StartsWith('/'))
        {
            return redactor.TryRedact(value, out redacted);
        }

        redacted = null;
        var scheme = value.IndexOf("://", StringComparison.Ordinal);
        var pathStart = scheme < 0 ? -1 : value.IndexOf('/', scheme + 3);
        if (pathStart < 0)
        {
            return false;
        }

        var fragment = value.IndexOf('#', pathStart);
        var pathAndQuery = fragment < 0 ? value[pathStart..] : value[pathStart..fragment];
        if (!redactor.TryRedact(pathAndQuery, out var redactedPath))
        {
            return false;
        }

        redacted = string.Concat(value.AsSpan(0, pathStart), redactedPath, fragment < 0 ? "" : value[fragment..]);
        return true;
    }
}

/// <summary>
/// Redacts credential paths in a span's tags and display name. Registered ahead of the exporters,
/// and a processor rather than an instrumentation enrich hook so it runs after every tag is set.
/// </summary>
internal sealed class RequestPathRedactingActivityProcessor(IRequestPathRedactor? redactor) : BaseProcessor<Activity>
{
    public override void OnEnd(Activity data)
    {
        if (redactor is null)
        {
            return;
        }

        List<(string Key, string Raw, string Redacted)>? redactedTags = null;
        foreach (var (key, value) in data.TagObjects)
        {
            if (value is string raw && redactor.TryRedactValue(raw, out var redacted))
            {
                (redactedTags ??= []).Add((key, raw, redacted));
            }
        }

        foreach (var (key, raw, redacted) in redactedTags ?? [])
        {
            data.SetTag(key, redacted);
            data.DisplayName = data.DisplayName.Replace(raw, redacted, StringComparison.Ordinal);
        }
    }
}

/// <summary>
/// Pushes a copy of each scope with its credential paths redacted, since the hosting scope captures
/// the raw path before routing has run and the logger has no later hook to rewrite it.
/// </summary>
/// <remarks>
/// Replacing the logger factory's own scope provider drops the <c>ActivityTrackingOptions</c>
/// scope; OpenTelemetry log records carry the trace and span ids themselves.
/// </remarks>
internal sealed class RequestPathRedactingScopeProvider(IRequestPathRedactor redactor) : IExternalScopeProvider
{
    private readonly LoggerExternalScopeProvider _inner = new();

    public void ForEachScope<TState>(Action<object?, TState> callback, TState state) =>
        _inner.ForEachScope(callback, state);

    public IDisposable Push(object? state) =>
        _inner.Push(
            state is IReadOnlyList<KeyValuePair<string, object?>> values
            && RequestPathRedactingLogProcessor.TryRedact(redactor, values, out var redacted)
                ? new RedactedScope(redacted)
                : state
        );

    private sealed class RedactedScope(List<KeyValuePair<string, object?>> values)
        : IReadOnlyList<KeyValuePair<string, object?>>
    {
        public KeyValuePair<string, object?> this[int index] => values[index];

        public int Count => values.Count;

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => values.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString() => string.Join(" ", values.Select(v => $"{v.Key}:{v.Value}"));
    }
}

/// <summary>
/// Redacts credential paths in a record's attributes and formatted message. Registered ahead of
/// the exporters so they only ever see the redacted record.
/// </summary>
internal sealed class RequestPathRedactingLogProcessor(IRequestPathRedactor? redactor) : BaseProcessor<LogRecord>
{
    public override void OnEnd(LogRecord data)
    {
        if (redactor is null || data.Attributes is not { } attributes || !TryRedact(redactor, attributes, out var redacted))
        {
            return;
        }

        if (data.FormattedMessage is { } message)
        {
            for (var i = 0; i < attributes.Count; i++)
            {
                if (attributes[i].Value is string raw && !Equals(raw, redacted[i].Value))
                {
                    message = message.Replace(raw, (string)redacted[i].Value!, StringComparison.Ordinal);
                }
            }

            data.FormattedMessage = message;
        }

        data.Attributes = redacted;
    }

    internal static bool TryRedact(
        IRequestPathRedactor redactor,
        IReadOnlyList<KeyValuePair<string, object?>> values,
        [NotNullWhen(true)] out List<KeyValuePair<string, object?>>? redacted
    )
    {
        redacted = null;
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i].Value is string value && redactor.TryRedactValue(value, out var redactedValue))
            {
                redacted ??= [.. values];
                redacted[i] = new(values[i].Key, redactedValue);
            }
        }

        return redacted is not null;
    }
}

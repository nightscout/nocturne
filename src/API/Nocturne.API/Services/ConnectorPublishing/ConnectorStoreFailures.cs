using System.Collections.Concurrent;
using System.Reflection;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Infrastructure.Data;

namespace Nocturne.API.Services.ConnectorPublishing;

/// <summary>
///     A store a connector holds (<typeparamref name="TStore"/>: one of the publisher interfaces, the
///     state store, or the Google Health writer): the scope's store, with a failure of the database
///     (<see cref="StoreFailure"/>) raised as <see cref="ConnectorStoreUnavailableException"/>, the
///     one store failure the connector layer can name without referencing EF. Every other exception
///     passes through unchanged. The API's own readers take the undecorated store and see the raw
///     failure.
/// </summary>
/// <remarks>
///     One proxy serves every store, so a method added to an interface is translated without a line of
///     code, and <see cref="Translated{TResult}"/> is the one place the rule is applied. Every method of
///     a store returns a <see cref="Task"/> or a <see cref="Task{TResult}"/>.
/// </remarks>
public class ConnectorStoreFailures<TStore> : DispatchProxy
    where TStore : class
{
    private static readonly ConcurrentDictionary<Type, MethodInfo> TranslatedOf = new();

    private static readonly MethodInfo TranslatedMethod =
        typeof(ConnectorStoreFailures<TStore>).GetMethod(nameof(Translated), BindingFlags.NonPublic | BindingFlags.Static)!;

    private TStore _inner = null!;

    /// <summary>The store a connector holds in place of <paramref name="inner"/>.</summary>
    public static TStore Over(TStore inner)
    {
        var proxy = Create<TStore, ConnectorStoreFailures<TStore>>();
        ((ConnectorStoreFailures<TStore>)(object)proxy)._inner = inner;
        return proxy;
    }

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var method = targetMethod ?? throw new ArgumentNullException(nameof(targetMethod));
        object? Call() => method.Invoke(_inner, BindingFlags.DoNotWrapExceptions, null, args, null);

        var returns = method.ReturnType;
        if (returns == typeof(Task))
            return Translated<bool>(() => Completed((Task)Call()!));

        if (returns.IsGenericType && returns.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var translated = TranslatedOf.GetOrAdd(
                returns.GetGenericArguments()[0],
                result => TranslatedMethod.MakeGenericMethod(result));
            return translated.Invoke(null, [(Func<object?>)Call]);
        }

        throw new NotSupportedException($"{typeof(TStore).Name}.{method.Name} does not return a Task.");
    }

    private static async Task<bool> Completed(Task task)
    {
        await task;
        return true;
    }

    private static async Task<TResult> Translated<TResult>(Func<object?> call)
    {
        try
        {
            return await (Task<TResult>)call()!;
        }
        catch (Exception ex) when (StoreFailure.Is(ex))
        {
            throw new ConnectorStoreUnavailableException(ex.Message, ex);
        }
    }
}

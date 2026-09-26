using System.Reflection;

namespace Nocturne.Tests.Shared.Infrastructure;

/// <summary>
/// Truncates every <see cref="DateTime"/> argument to microseconds as Npgsql does. SQLite keeps full ticks.
/// </summary>
public class MicrosecondPrecision<T> : DispatchProxy where T : class
{
    private T _inner = null!;

    public static T Wrap(T inner)
    {
        var proxy = Create<T, MicrosecondPrecision<T>>();
        ((MicrosecondPrecision<T>)(object)proxy)._inner = inner;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        for (var i = 0; i < args!.Length; i++)
            if (args[i] is DateTime d)
                args[i] = d.AddTicks(-(d.Ticks % 10));

        try
        {
            return method!.Invoke(_inner, args);
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            throw e.InnerException;
        }
    }
}

namespace Nocturne.API.Helpers;

/// <summary>Starts work that outlives its caller without the caller's execution context.</summary>
internal static class ExecutionFlow
{
    /// <summary>
    /// Runs <paramref name="start"/> with the execution context's flow suppressed, so the timers and tasks it starts
    /// carry none of the caller's async locals: not its <c>HttpContext</c>, its activity or its logging scopes.
    /// </summary>
    public static T WithoutFlow<T>(Func<T> start)
    {
        if (ExecutionContext.IsFlowSuppressed())
            return start();
        using (ExecutionContext.SuppressFlow())
            return start();
    }
}

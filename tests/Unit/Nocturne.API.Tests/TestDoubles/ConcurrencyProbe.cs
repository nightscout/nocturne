namespace Nocturne.API.Tests.TestDoubles;

/// <summary>Counts the callers inside a region at once and keeps the most it ever saw.</summary>
internal sealed class ConcurrencyProbe
{
    private int inFlight;
    private int peak;

    public int Peak => Volatile.Read(ref peak);

    public IDisposable Enter()
    {
        var now = Interlocked.Increment(ref inFlight);
        int seen;
        while (now > (seen = Volatile.Read(ref peak)) && Interlocked.CompareExchange(ref peak, now, seen) != seen) { }
        return new Exit(this);
    }

    private sealed class Exit(ConcurrencyProbe probe) : IDisposable
    {
        public void Dispose() => Interlocked.Decrement(ref probe.inFlight);
    }
}

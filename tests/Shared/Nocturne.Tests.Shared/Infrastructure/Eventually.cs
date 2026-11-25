using System.Diagnostics;

namespace Nocturne.Tests.Shared.Infrastructure;

/// <summary>
/// Polls until a condition holds, for state a test cannot await as an event or by moving a fake
/// clock. The patience is a hang guard, not a pass or fail budget: it is long enough that a loaded
/// runner never misses it, and a wait that outlasts it throws a <see cref="TimeoutException"/>
/// naming what was awaited. Pass a shorter patience only where how long nothing happens is itself
/// what the test asserts.
/// </summary>
public static class Eventually
{
    public static readonly TimeSpan DefaultPatience = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan DefaultStep = TimeSpan.FromMilliseconds(10);

    /// <param name="description">What is awaited, as the clause after "waiting for".</param>
    /// <param name="eachStep">Runs after each check that fails, before the pause, e.g. to move a fake clock.</param>
    /// <param name="lastSeen">Read when the wait times out, to say what the last check saw.</param>
    public static Task UntilAsync(
        string description,
        Func<bool> condition,
        TimeSpan? patience = null,
        TimeSpan? step = null,
        Action? eachStep = null,
        Func<string>? lastSeen = null,
        CancellationToken cancellationToken = default) =>
        UntilAsync(description, () => Task.FromResult(condition()), patience, step, eachStep, lastSeen, cancellationToken);

    /// <inheritdoc cref="UntilAsync(string, Func{bool}, TimeSpan?, TimeSpan?, Action?, Func{string}?, CancellationToken)"/>
    public static async Task UntilAsync(
        string description,
        Func<Task<bool>> condition,
        TimeSpan? patience = null,
        TimeSpan? step = null,
        Action? eachStep = null,
        Func<string>? lastSeen = null,
        CancellationToken cancellationToken = default)
    {
        if (!await HoldsAsync(condition, patience, step, eachStep, cancellationToken))
            throw new TimeoutException(Failure(description, patience, lastSeen));
    }

    /// <summary>
    /// Polls <paramref name="probe"/> until it returns a value and returns that value.
    /// </summary>
    public static async Task<T> ValueAsync<T>(
        string description,
        Func<Task<T?>> probe,
        TimeSpan? patience = null,
        TimeSpan? step = null,
        Action? eachStep = null,
        Func<string>? lastSeen = null,
        CancellationToken cancellationToken = default)
        where T : class
    {
        T? found = null;
        await UntilAsync(
            description, async () => (found = await probe()) is not null, patience, step, eachStep, lastSeen, cancellationToken);
        return found!;
    }

    /// <summary>
    /// Returns whether the condition held within <paramref name="patience"/>, for a test that accepts
    /// either outcome.
    /// </summary>
    public static Task<bool> HoldsWithinAsync(
        Func<bool> condition,
        TimeSpan patience,
        TimeSpan? step = null,
        CancellationToken cancellationToken = default) =>
        HoldsAsync(() => Task.FromResult(condition()), patience, step, null, cancellationToken);

    private static async Task<bool> HoldsAsync(
        Func<Task<bool>> condition,
        TimeSpan? patience,
        TimeSpan? step,
        Action? eachStep,
        CancellationToken cancellationToken)
    {
        var limit = patience ?? DefaultPatience;
        var clock = Stopwatch.StartNew();
        while (!await condition())
        {
            if (clock.Elapsed >= limit)
                return false;

            eachStep?.Invoke();
            await Task.Delay(step ?? DefaultStep, cancellationToken);
        }

        return true;
    }

    private static string Failure(string description, TimeSpan? patience, Func<string>? lastSeen) =>
        $"Timed out after {(patience ?? DefaultPatience).TotalSeconds:0.#} s waiting for {description}."
        + (lastSeen is null ? string.Empty : $" Last seen: {lastSeen()}.");
}

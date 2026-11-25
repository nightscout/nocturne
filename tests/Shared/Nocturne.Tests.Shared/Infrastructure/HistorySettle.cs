using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Services;

namespace Nocturne.Tests.Shared.Infrastructure;

/// <summary>
/// Lets a test read history straight after a write. A history read delivers only stamps below its
/// horizon, which is truncated to the millisecond, so a row stamped in the millisecond of the read
/// is left for the next one.
/// </summary>
public static class HistorySettle
{
    /// <summary>
    /// Returns once the history horizon is past every write the test has made. A test that reads
    /// straight after a write waits here first, then reads once and asserts the whole page. Rows are
    /// stamped on the application clock, so the horizon is compared with that clock: a database clock
    /// that drifts from it only lengthens or shortens the wait, never admits a stamp the horizon is
    /// behind.
    /// </summary>
    /// <param name="services">A provider scoped to the tenant the read is for: the horizon waits for that tenant's writers.</param>
    public static async Task UntilHorizonPassesAsync(IServiceProvider services)
    {
        var written = DateTime.UtcNow;
        var contexts = services.GetRequiredService<INocturneDbContextFactory>();
        await Eventually.UntilAsync(
            "the history horizon to pass the last write",
            async () =>
            {
                await using var ctx = await contexts.CreateAsync();
                var horizon = await HistoryHorizon.ReadAsync(ctx, TimeSpan.Zero, NullLogger.Instance, CancellationToken.None);
                return horizon > written;
            },
            step: TimeSpan.FromMilliseconds(1));
    }
}

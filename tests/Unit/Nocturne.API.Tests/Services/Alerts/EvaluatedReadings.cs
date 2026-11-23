using Nocturne.API.Services.Alerts;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Glucose;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary><see cref="IEvaluatedReadingStore"/> doubles for hand-built alert services.</summary>
internal static class EvaluatedReadings
{
    /// <summary>
    /// The newest reading the alert contexts select is always recorded as evaluated, as when every
    /// write's evaluation completes, so a sweep test drives only the passes it is about.
    /// </summary>
    public static IEvaluatedReadingStore EveryNewest(ICanonicalGlucoseService canonical, TimeProvider time) =>
        new Newest(canonical, time);

    private sealed class Newest(ICanonicalGlucoseService canonical, TimeProvider time) : IEvaluatedReadingStore
    {
        public async Task<EvaluatedReading?> GetAsync(CancellationToken ct) =>
            (await AlertReadings.SelectAsync(canonical, time.GetUtcNow().UtcDateTime, ct)).Selected is { } selected
                ? EvaluatedReading.Of(selected)
                : null;

        public Task RecordAsync(EvaluatedReading reading, CancellationToken ct) => Task.CompletedTask;
    }
}

using Nocturne.Core.Models.Analytics;

namespace Nocturne.Core.Contracts.Analytics;

/// <summary>
/// Lists what the sensor flagged in a window, grouped into episodes
/// (<c>ErroneousReadingEpisodes</c>). Read-only.
/// </summary>
public interface IErroneousReadingsService
{
    Task<ErroneousReadingsReport> GetAsync(DateTime from, DateTime to, CancellationToken ct = default);
}

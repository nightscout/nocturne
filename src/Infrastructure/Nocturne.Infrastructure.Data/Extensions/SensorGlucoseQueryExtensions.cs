using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Entities.V4;

namespace Nocturne.Infrastructure.Data.Extensions;

/// <summary>
/// Query form of <see cref="SensorGlucoseQualityRules.CountsInStatistics"/>.
/// </summary>
public static class SensorGlucoseQueryExtensions
{
    public static IQueryable<SensorGlucoseEntity> CountedInStatistics(this IQueryable<SensorGlucoseEntity> query) =>
        query.Where(e => e.Quality == null || e.Quality != SensorGlucoseQualityRules.InvalidStored);
}

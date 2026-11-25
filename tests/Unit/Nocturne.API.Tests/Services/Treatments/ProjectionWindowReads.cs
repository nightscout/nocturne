using Moq;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Models;

namespace Nocturne.API.Tests.Services.Treatments;

/// <summary>The Moq setup and verification of <see cref="IV4ToLegacyProjectionService.GetProjectedTreatmentsAsync"/>.</summary>
internal static class ProjectionWindowReads
{
    /// <summary>The range of a window read between two instants given in Unix milliseconds.</summary>
    public static LegacyTreatmentRange Range(
        long? fromMills, long? toMills, int limit, bool descending = true, bool includeDeleted = false) =>
        LegacyTreatmentRange.FromMills(fromMills, toMills, limit, descending, includeDeleted);

    /// <summary>Serves <paramref name="treatments"/> to each window read that <paramref name="when"/> accepts, every read when null.</summary>
    public static void SetupWindowRead(
        this Mock<IV4ToLegacyProjectionService> projection,
        IEnumerable<Treatment> treatments,
        Predicate<LegacyTreatmentRange>? when = null) =>
        projection.SetupWindowRead(_ => treatments, when);

    /// <summary>Serves what <paramref name="serve"/> returns for the range of each window read that <paramref name="when"/> accepts, every read when null.</summary>
    public static void SetupWindowRead(
        this Mock<IV4ToLegacyProjectionService> projection,
        Func<LegacyTreatmentRange, IEnumerable<Treatment>> serve,
        Predicate<LegacyTreatmentRange>? when = null) =>
        projection
            .Setup(p => p.GetProjectedTreatmentsAsync(Accepted(when), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LegacyTreatmentRange range, CancellationToken _) => serve(range));

    public static void VerifyWindowRead(
        this Mock<IV4ToLegacyProjectionService> projection,
        Predicate<LegacyTreatmentRange>? when,
        Times times) =>
        projection.Verify(p => p.GetProjectedTreatmentsAsync(Accepted(when), It.IsAny<CancellationToken>()), times);

    private static LegacyTreatmentRange Accepted(Predicate<LegacyTreatmentRange>? when) =>
        Match.Create(when ?? (_ => true));
}

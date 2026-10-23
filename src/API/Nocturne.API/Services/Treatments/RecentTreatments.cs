using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Treatments;

/// <summary>
/// The treatments the IOB and COB calculators read for the present moment: insulin over the last
/// eight hours and carbs over the last six.
/// </summary>
internal static class RecentTreatments
{
    private const int Limit = 1000;
    private static readonly TimeSpan InsulinWindow = TimeSpan.FromHours(8);
    private static readonly TimeSpan CarbWindow = TimeSpan.FromHours(6);

    public static async Task<(List<Bolus> Boluses, List<TempBasal> TempBasals)> LoadInsulinAsync(
        IBolusRepository bolusRepository,
        ITempBasalRepository tempBasalRepository,
        CancellationToken ct = default
    )
    {
        var from = DateTime.UtcNow - InsulinWindow;
        var boluses = await bolusRepository.GetAsync(
            from: from, to: null, device: null, source: null,
            limit: Limit, offset: 0, descending: false, ct: ct
        );
        var tempBasals = await tempBasalRepository.GetAsync(
            from: from, to: null, device: null, source: null,
            limit: Limit, offset: 0, descending: false, ct: ct
        );
        return (boluses.ToList(), tempBasals.ToList());
    }

    public static async Task<List<CarbIntake>> LoadCarbsAsync(
        ICarbIntakeRepository carbIntakeRepository,
        CancellationToken ct = default
    ) =>
        (
            await carbIntakeRepository.GetAsync(
                from: DateTime.UtcNow - CarbWindow, to: null, device: null, source: null,
                limit: Limit, offset: 0, descending: false, ct: ct
            )
        ).ToList();
}

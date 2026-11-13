using Nocturne.API.Services.Platform;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Treatments;

/// <inheritdoc cref="IMealReadService" />
public sealed class MealReadService : IMealReadService
{
    private readonly ICarbIntakeRepository _carbIntakes;
    private readonly IBolusRepository _boluses;
    private readonly ITreatmentFoodService _treatmentFoods;
    private readonly IDemoModeService _demoMode;

    public MealReadService(
        ICarbIntakeRepository carbIntakes,
        IBolusRepository boluses,
        ITreatmentFoodService treatmentFoods,
        IDemoModeService demoMode)
    {
        _carbIntakes = carbIntakes;
        _boluses = boluses;
        _treatmentFoods = treatmentFoods;
        _demoMode = demoMode;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MealEvent>> GetMealsAsync(
        DateTime from, DateTime to, bool? attributed, CancellationToken ct = default)
    {
        var carbIntakes = await _carbIntakes.GetMealCandidatesAsync(from, to, _demoMode.IsEnabled, ct);
        if (carbIntakes.Count == 0)
            return [];

        var foodEntries = await _treatmentFoods.GetByCarbIntakeIdsAsync(carbIntakes.Select(c => c.Id), ct);
        var foodsByCarbIntake = foodEntries
            .GroupBy(f => f.CarbIntakeId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var correlationIds = carbIntakes
            .Where(c => c.CorrelationId.HasValue)
            .Select(c => c.CorrelationId!.Value)
            .Distinct()
            .ToList();

        var bolusesByCorrelation = (await _boluses.GetByCorrelationIdsAsync(correlationIds, ct))
            .GroupBy(b => b.CorrelationId!.Value)
            .ToDictionary(g => g.Key, g => g.ToArray());

        var events = new List<MealEvent>();

        foreach (var group in carbIntakes.Where(c => c.CorrelationId.HasValue).GroupBy(c => c.CorrelationId!.Value))
        {
            events.Add(BuildEvent(
                group.Key,
                group.ToList(),
                bolusesByCorrelation.TryGetValue(group.Key, out var boluses) ? boluses : [],
                foodsByCarbIntake));
        }

        foreach (var orphan in carbIntakes.Where(c => !c.CorrelationId.HasValue))
            events.Add(BuildEvent(null, [orphan], [], foodsByCarbIntake));

        var matching = attributed.HasValue
            ? events.Where(e => e.IsAttributed == attributed.Value)
            : events;

        return matching.OrderByDescending(e => e.Timestamp).ToList();
    }

    private static MealEvent BuildEvent(
        Guid? correlationId,
        IReadOnlyList<CarbIntake> carbIntakes,
        Bolus[] boluses,
        IReadOnlyDictionary<Guid, List<TreatmentFood>> foodsByCarbIntake)
    {
        var foods = carbIntakes
            .SelectMany(c => foodsByCarbIntake.TryGetValue(c.Id, out var list) ? list : [])
            .ToArray();

        var totalCarbs = carbIntakes.Sum(c => c.Carbs);
        var attributedCarbs = (double)foods.Sum(f => f.Carbs);

        var earliestCarb = carbIntakes.Min(c => c.Timestamp);
        var timestamp = boluses.Length > 0
            ? (boluses.Min(b => b.Timestamp) < earliestCarb ? boluses.Min(b => b.Timestamp) : earliestCarb)
            : earliestCarb;

        return new MealEvent
        {
            CorrelationId = correlationId,
            Timestamp = timestamp,
            CarbIntakes = carbIntakes.ToArray(),
            Boluses = boluses,
            Foods = foods,
            TotalCarbs = totalCarbs,
            AttributedCarbs = attributedCarbs,
            UnspecifiedCarbs = totalCarbs - attributedCarbs,
            TotalInsulin = boluses.Sum(b => b.Insulin),
            IsAttributed = foods.Length > 0,
        };
    }
}

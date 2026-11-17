using Nocturne.Core.Models.V4;

namespace Nocturne.Core.Contracts.Treatments;

/// <summary>
/// Reads meal events: carb intakes grouped with the boluses sharing their correlation identifier.
/// </summary>
/// <seealso cref="MealEvent"/>
public interface IMealReadService
{
    /// <summary>
    /// The meal events whose carb intakes fall in [<paramref name="from"/>, <paramref name="to"/>], newest
    /// first. An intake with no correlation identifier is an event of its own. Demo mode returns only demo
    /// records, and otherwise demo records are left out.
    /// </summary>
    /// <param name="from">Inclusive start of the window.</param>
    /// <param name="to">Inclusive end of the window.</param>
    /// <param name="attributed">When set, keeps only events whose <see cref="MealEvent.IsAttributed"/> matches.</param>
    Task<IReadOnlyList<MealEvent>> GetMealsAsync(
        DateTime from,
        DateTime to,
        bool? attributed,
        CancellationToken ct = default
    );
}

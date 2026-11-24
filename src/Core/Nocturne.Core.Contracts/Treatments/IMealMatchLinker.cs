using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Models;

namespace Nocturne.Core.Contracts.Treatments;

/// <summary>
/// Links a connector food entry to a carb intake. It holds no notification dependency, so a
/// notification action handler, which <c>InAppNotificationService</c> receives, can accept a match
/// without a cycle through <see cref="IMealMatchingService"/>.
/// </summary>
/// <seealso cref="IMealMatchingService"/>
public interface IMealMatchLinker
{
    /// <summary>
    /// Writes the carb breakdown row and marks the food entry matched, as one claim.
    /// </summary>
    /// <param name="foodEntryId">The connector food entry ID.</param>
    /// <param name="carbIntakeId">The carb intake to link the food to.</param>
    /// <param name="carbs">The carb amount (may be adjusted from original).</param>
    /// <param name="timeOffsetMinutes">Minutes offset from the carb intake time (0 = ate at bolus time).</param>
    /// <returns>
    /// What the accept did. Only <see cref="AcceptMatchOutcome.Linked"/> wrote a row, so a caller
    /// counting links counts that outcome alone. See <see cref="IConnectorFoodEntryRepository.AcceptMatchAsync"/>
    /// for the others.
    /// </returns>
    Task<AcceptMatchOutcome> LinkAsync(
        Guid foodEntryId,
        Guid carbIntakeId,
        decimal carbs,
        int timeOffsetMinutes,
        CancellationToken ct = default);
}

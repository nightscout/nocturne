using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Twiist.Models;
using Nocturne.Core.Constants;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.Twiist.Mappers;

/// <summary>
/// Maps Twiist insulin dose history to V4 Bolus models.
/// Only bolus-type doses are mapped; basal/temp-basal/suspend/resume are skipped.
/// </summary>
public class TwiistInsulinMapper(ILogger logger)
{
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Maps Twiist insulin doses to Bolus records, filtering to bolus-type doses only.
    /// </summary>
    public IEnumerable<Bolus> MapBoluses(List<TwiistInsulinDose>? doses)
    {
        if (doses == null || doses.Count == 0) return [];

        var now = DateTime.UtcNow;

        return doses
            .Where(d => IsBolusDose(d.DoseType))
            .Select(d => ConvertBolus(d, now))
            .Where(b => b != null)
            .Cast<Bolus>()
            .OrderBy(b => b.Mills)
            .ToList();
    }

    private Bolus? ConvertBolus(TwiistInsulinDose dose, DateTime now)
    {
        try
        {
            if (dose.StartDate == null || dose.Value == null)
                return null;

            var timestamp = dose.StartDate.Value.ToUniversalTime();

            return new Bolus
            {
                Id = Guid.CreateVersion7(),
                Timestamp = timestamp,
                LegacyId = $"twiist_bolus_{dose.Identifier ?? timestamp.Ticks.ToString()}",
                Device = DataSources.TwiistConnector,
                DataSource = DataSources.TwiistConnector,
                Insulin = (double)dose.Value,
                Programmed = (double)dose.Value,
                Delivered = (double)dose.Value,
                BolusType = BolusType.Normal,
                Kind = BolusKind.Manual,
                CreatedAt = now,
                ModifiedAt = now
            };
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Error converting Twiist insulin dose: {Identifier}", dose.Identifier);
            return null;
        }
        catch (FormatException ex)
        {
            _logger.LogWarning(ex, "Error converting Twiist insulin dose: {Identifier}", dose.Identifier);
            return null;
        }
        catch (OverflowException ex)
        {
            _logger.LogWarning(ex, "Error converting Twiist insulin dose: {Identifier}", dose.Identifier);
            return null;
        }
    }

    private static bool IsBolusDose(string? doseType)
    {
        if (string.IsNullOrEmpty(doseType))
            return false;

        return doseType.Equals("bolus", StringComparison.OrdinalIgnoreCase);
    }
}

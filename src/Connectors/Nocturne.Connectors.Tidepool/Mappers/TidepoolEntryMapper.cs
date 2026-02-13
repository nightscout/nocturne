using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Tidepool.Configurations;
using Nocturne.Connectors.Tidepool.Models;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.Tidepool.Mappers;

public class TidepoolEntryMapper(ILogger logger, string connectorSource)
{
    private readonly string _connectorSource =
        connectorSource ?? throw new ArgumentNullException(nameof(connectorSource));

    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public IEnumerable<Entry> MapBgValues(TidepoolBgValue[]? bgValues)
    {
        if (bgValues == null || bgValues.Length == 0) return [];

        return bgValues
            .Where(bg => bg.Value > 0 && bg.Time.HasValue)
            .Select(ConvertBgValue)
            .Where(e => e.Sgv > 0)
            .OrderBy(e => e.Date)
            .ToList();
    }

    private Entry ConvertBgValue(TidepoolBgValue bgValue)
    {
        try
        {
            var mgdlValue = ConvertToMgdl(bgValue.Value, bgValue.Units);

            return new Entry
            {
                Id = $"tidepool_{bgValue.Id}",
                Date = bgValue.Time!.Value,
                Sgv = (int)Math.Round(mgdlValue),
                Direction = Direction.NONE.ToString(),
                Device = _connectorSource,
                Type = "sgv"
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error converting Tidepool BG value: {Id}", bgValue.Id);
            return new Entry { Type = "sgv", Device = _connectorSource };
        }
    }

    /// <summary>
    ///     Converts a blood glucose value to mg/dL.
    ///     Tidepool stores values in mmol/L or mg/dL depending on the user's settings.
    /// </summary>
    internal static double ConvertToMgdl(double value, string units)
    {
        if (string.IsNullOrEmpty(units))
            return value;

        return units.ToLowerInvariant() switch
        {
            "mmol/l" or "mmol" => value * TidepoolConstants.MmolToMgdlFactor,
            "mg/dl" => value,
            _ => value
        };
    }
}

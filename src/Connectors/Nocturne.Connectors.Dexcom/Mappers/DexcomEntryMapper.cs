using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Dexcom.Models;
using Nocturne.Connectors.Dexcom.Utilities;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.Dexcom.Mappers;

public class DexcomEntryMapper(ILogger logger, string connectorSource)
{
    private static readonly Dictionary<int, Direction> TrendDirections = new()
    {
        { 0, Direction.NONE },
        { 1, Direction.DoubleUp },
        { 2, Direction.SingleUp },
        { 3, Direction.FortyFiveUp },
        { 4, Direction.Flat },
        { 5, Direction.FortyFiveDown },
        { 6, Direction.SingleDown },
        { 7, Direction.DoubleDown },
        { 8, Direction.NotComputable },
        { 9, Direction.RateOutOfRange }
    };

    private readonly string _connectorSource =
        connectorSource ?? throw new ArgumentNullException(nameof(connectorSource));

    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public IEnumerable<Entry> TransformBatchDataToEntries(DexcomEntry[]? batchData)
    {
        if (batchData == null || batchData.Length == 0) return [];

        return batchData
            .Where(entry => entry.Value > 0)
            .Select(ConvertDexcomEntry)
            .OrderBy(entry => entry.Date)
            .ToList();
    }

    private Entry ConvertDexcomEntry(DexcomEntry dexcomEntry)
    {
        try
        {
            if (!DexcomTimestampParser.TryParse(dexcomEntry.Wt, out var timestamp))
            {
                _logger.LogWarning(
                    "Could not parse Dexcom timestamp: {Timestamp}",
                    dexcomEntry.Wt
                );
                return new Entry { Type = "sgv", Device = _connectorSource };
            }

            var direction = TrendDirections.GetValueOrDefault(
                dexcomEntry.Trend,
                Direction.NotComputable
            );

            // Generate deterministic ID based on device + timestamp for deduplication
            var id = $"dexcom_{dexcomEntry.Wt}";

            return new Entry
            {
                Id = id,
                Date = timestamp,
                Sgv = dexcomEntry.Value,
                Direction = direction.ToString(),
                Device = _connectorSource,
                Type = "sgv"
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error converting Dexcom entry: {@Entry}", dexcomEntry);
            return new Entry { Type = "sgv", Device = _connectorSource };
        }
    }
}
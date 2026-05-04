using Nocturne.Core.Constants;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4.Repositories;

namespace Nocturne.Services.Demo.Services;

/// <summary>
/// Service for managing demo entries in the database via the V4 SensorGlucose repository.
/// </summary>
public interface IDemoEntryService
{
    Task CreateEntriesAsync(
        IEnumerable<Entry> entries,
        CancellationToken cancellationToken = default
    );
    Task<long> DeleteAllDemoEntriesAsync(CancellationToken cancellationToken = default);
    Task<bool> HasDemoEntriesAsync(CancellationToken cancellationToken = default);
}

public class DemoEntryService : IDemoEntryService
{
    private readonly ISensorGlucoseRepository _sensorGlucoseRepository;
    private readonly ILogger<DemoEntryService> _logger;

    public DemoEntryService(ISensorGlucoseRepository sensorGlucoseRepository, ILogger<DemoEntryService> logger)
    {
        _sensorGlucoseRepository = sensorGlucoseRepository;
        _logger = logger;
    }

    public async Task CreateEntriesAsync(
        IEnumerable<Entry> entries,
        CancellationToken cancellationToken = default
    )
    {
        var sensorGlucoseRecords = entries
            .Where(e => e.Type == "sgv")
            .Select(MapEntryToSensorGlucose)
            .ToList();

        if (sensorGlucoseRecords.Count == 0)
            return;

        await _sensorGlucoseRepository.BulkCreateAsync(sensorGlucoseRecords, cancellationToken);
        _logger.LogDebug("Created {Count} demo sensor glucose records", sensorGlucoseRecords.Count);
    }

    public async Task<long> DeleteAllDemoEntriesAsync(CancellationToken cancellationToken = default)
    {
        var count = await _sensorGlucoseRepository.DeleteBySourceAsync(
            DataSources.DemoService,
            cancellationToken
        );
        _logger.LogInformation("Deleted {Count} demo sensor glucose records", count);
        return count;
    }

    public async Task<bool> HasDemoEntriesAsync(CancellationToken cancellationToken = default)
    {
        var latest = await _sensorGlucoseRepository.GetLatestTimestampAsync(
            DataSources.DemoService,
            cancellationToken
        );
        return latest.HasValue;
    }

    private static SensorGlucose MapEntryToSensorGlucose(Entry entry)
    {
        var now = DateTime.UtcNow;
        var timestamp = entry.Date ?? DateTimeOffset.FromUnixTimeMilliseconds(entry.Mills).UtcDateTime;

        return new SensorGlucose
        {
            Id = Guid.CreateVersion7(),
            Timestamp = timestamp,
            Device = entry.Device,
            DataSource = entry.DataSource ?? DataSources.DemoService,
            Mgdl = entry.Sgv ?? entry.Mgdl,
            Direction = ParseDirection(entry.Direction),
            Delta = entry.Delta,
            Noise = entry.Noise,
            Filtered = entry.Filtered,
            Unfiltered = entry.Unfiltered,
            CreatedAt = now,
            ModifiedAt = now,
        };
    }

    private static GlucoseDirection? ParseDirection(string? direction)
    {
        if (string.IsNullOrEmpty(direction))
            return null;

        return Enum.TryParse<GlucoseDirection>(direction, ignoreCase: true, out var result)
            ? result
            : null;
    }
}

using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Glooko.Configurations;
using Nocturne.Connectors.Glooko.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Connectors.Core.Utilities;

namespace Nocturne.Connectors.Glooko.Mappers;

/// <summary>
///     Maps the SSV2 <c>notes</c> feed (app-logged free-text notes) to <see cref="Note"/> records.
///     Keyed on the stable Glooko guid (raw-timestamp hash fallback) so re-correction upserts in place;
///     soft-delete aware and skips empty text.
/// </summary>
public class GlookoNoteMapper(string connectorSource, GlookoTimeMapper timeMapper, ILogger logger)
{
    private static readonly ConnectorLegacyIds LegacyIds = ConnectorLegacyIds.For<GlookoConnectorConfiguration>();

    private readonly string _connectorSource = connectorSource ?? throw new ArgumentNullException(nameof(connectorSource));
    private readonly GlookoTimeMapper _timeMapper = timeMapper ?? throw new ArgumentNullException(nameof(timeMapper));
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public List<Note> MapSsv2Notes(IReadOnlyList<GlookoSsv2Note> notes)
    {
        var results = new List<Note>();

        foreach (var note in ConnectorFaults.NonNullRecords(notes, _logger, _connectorSource, "SSV2 note"))
        {
            try
            {
                if (note.SoftDeleted || string.IsNullOrWhiteSpace(note.Value)) continue;

                var rawTimestamp = _timeMapper.GetRawGlookoDate(note.Timestamp ?? string.Empty, null);
                var correctedTimestamp = _timeMapper.GetCorrectedGlookoTime(rawTimestamp);

                var legacyId = !string.IsNullOrEmpty(note.Guid)
                    ? LegacyIds.Of("note", note.Guid)
                    : LegacyIds.Hashed("ssv2_note", rawTimestamp.Ticks, note.Value);

                results.Add(new Note
                {
                    Timestamp = correctedTimestamp,
                    LegacyId = legacyId,
                    SyncIdentifier = legacyId,
                    Device = _connectorSource,
                    Text = note.Value!,
                });
            }
            catch (Exception ex) when (ConnectorFaults.IsMalformedRecord(ex))
            {
                _logger.LogWarning(ex, "[{ConnectorSource}] Skipped malformed SSV2 note {Guid}", _connectorSource, note.Guid);
            }
        }

        _logger.LogInformation(
            "[{ConnectorSource}] Transformed {Count} notes from SSV2 notes feed", _connectorSource, results.Count);

        return results;
    }
}

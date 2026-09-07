using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Glooko.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.Glooko.Mappers;

/// <summary>
///     Maps the SSV2 <c>notes</c> feed (app-logged free-text notes) to <see cref="Note"/> records.
///     Keyed on the stable Glooko guid (raw-timestamp hash fallback) so re-correction upserts in place;
///     soft-delete aware and skips empty text.
/// </summary>
public class GlookoNoteMapper(string connectorSource, GlookoTimeMapper timeMapper, ILogger logger)
{
    private readonly string _connectorSource = connectorSource ?? throw new ArgumentNullException(nameof(connectorSource));
    private readonly GlookoTimeMapper _timeMapper = timeMapper ?? throw new ArgumentNullException(nameof(timeMapper));
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public List<Note> MapSsv2Notes(IReadOnlyList<GlookoSsv2Note> notes)
    {
        var results = new List<Note>();

        foreach (var note in notes)
        {
            try
            {
                if (note.SoftDeleted || string.IsNullOrWhiteSpace(note.Value)) continue;

                var rawTimestamp = _timeMapper.GetRawGlookoDate(note.Timestamp ?? string.Empty, null);
                var correctedTimestamp = _timeMapper.GetCorrectedGlookoTime(rawTimestamp);
                var now = DateTime.UtcNow;

                var legacyId = !string.IsNullOrEmpty(note.Guid)
                    ? $"glooko_note_{note.Guid}"
                    : GenerateLegacyId("ssv2_note", rawTimestamp, note.Value);

                results.Add(new Note
                {
                    Id = Guid.CreateVersion7(),
                    Timestamp = correctedTimestamp,
                    LegacyId = legacyId,
                    SyncIdentifier = legacyId,
                    Device = _connectorSource,
                    DataSource = _connectorSource,
                    Text = note.Value!,
                    CreatedAt = now,
                    ModifiedAt = now
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[{ConnectorSource}] Error mapping SSV2 note", _connectorSource);
            }
        }

        _logger.LogInformation(
            "[{ConnectorSource}] Transformed {Count} notes from SSV2 notes feed", _connectorSource, results.Count);

        return results;
    }

    private static string GenerateLegacyId(string eventType, DateTime timestamp, string? additionalData = null)
    {
        var dataToHash = $"glooko_{eventType}_{timestamp.Ticks}_{additionalData ?? string.Empty}";
        using var sha1 = SHA1.Create();
        var hashBytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(dataToHash));
        return $"glooko_{Convert.ToHexString(hashBytes).ToLowerInvariant()}";
    }
}

namespace Nocturne.Core.Models.Connectors;

/// <summary>A tenant's stored configuration of one connector.</summary>
/// <param name="ConnectorName">The canonical (lower-case) connector name.</param>
/// <param name="SecretsJson">The secrets as stored: a JSON object of encrypted values.</param>
/// <param name="SyncCursorsJson">The incremental-sync cursors, keyed by resource; null until the first cursor-based sync.</param>
/// <param name="BackfillLowWaterMarks">The backfill low-water marks, keyed by collection; null when none is held.</param>
public sealed record ConnectorConfigurationRecord(
    Guid TenantId,
    string ConnectorName,
    string ConfigurationJson,
    string SecretsJson,
    int SchemaVersion,
    DateTimeOffset LastModified,
    string? ModifiedBy,
    DateTime? LastSyncAttempt,
    DateTime? LastSuccessfulSync,
    string? LastErrorMessage,
    DateTime? LastErrorAt,
    bool IsHealthy,
    string? SyncCursorsJson,
    string? BackfillLowWaterMarks)
{
    /// <summary>
    /// The longest <see cref="LastErrorMessage"/> stored. A connector reports one error per failing
    /// type per chunk, so a long backfill against a persistently failing publisher joins a multi-KB
    /// string; the store cuts it to this length, outside a surrogate pair, and marks the cut, since an
    /// over-length value or a lone surrogate would fail the very write that records the failure.
    /// </summary>
    public const int LastErrorMessageMaxLength = 1000;

    /// <summary>Leaves <see cref="SecretsJson"/> out of the generated text form, so a logged record never carries it.</summary>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"TenantId = {TenantId}, ConnectorName = {ConnectorName}, SchemaVersion = {SchemaVersion}, ");
        builder.Append($"LastModified = {LastModified}, IsHealthy = {IsHealthy}, LastSuccessfulSync = {LastSuccessfulSync}");
        return true;
    }
}

/// <summary>
/// The health fields a sync run reports. A null field is left as stored; a <c>Clear</c> flag empties
/// its field and outranks the value beside it.
/// </summary>
public sealed record ConnectorHealthUpdate(
    DateTime? LastSyncAttempt = null,
    DateTime? LastSuccessfulSync = null,
    string? LastErrorMessage = null,
    bool ClearLastErrorMessage = false,
    DateTime? LastErrorAt = null,
    bool ClearLastErrorAt = false,
    bool? IsHealthy = null);

/// <summary>The stored record of a platform-admin connector cursor reset job.</summary>
/// <param name="State">The job state by name, as the startup sweep matches it.</param>
/// <param name="ConnectorsJson">The per-connector progress snapshot, serialized.</param>
public sealed record ConnectorResetJobRecord(
    Guid Id,
    Guid TenantId,
    string TenantSlug,
    string State,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    string? ErrorMessage,
    int TotalConnectors,
    int CompletedConnectors,
    string? ConnectorsJson);

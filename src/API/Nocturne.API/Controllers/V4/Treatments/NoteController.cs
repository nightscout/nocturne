using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Models.Requests.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Controllers.V4.Treatments;

/// <summary>
/// Controller for managing note observations.
/// Exposes standard V4 CRUD operations via <see cref="V4CrudControllerBase{TModel,TCreateRequest,TUpdateRequest,TRepository}"/>.
/// </summary>
/// <remarks>
/// Create and update use the same <see cref="UpsertNoteRequest"/> shape.
/// On update, immutable fields (<see cref="Note.CorrelationId"/>, <see cref="Note.LegacyId"/>,
/// <see cref="Note.CreatedAt"/>, <see cref="Note.SyncIdentifier"/>, and
/// <see cref="Note.AdditionalProperties"/>) are preserved from the existing record.
/// </remarks>
/// <seealso cref="INoteRepository"/>
/// <seealso cref="Note"/>
/// <seealso cref="UpsertNoteRequest"/>
[ApiController]
[Route("api/v4/observations/notes")]
[Authorize]
[Produces("application/json")]
public class NoteController(INoteRepository repo)
    : V4CrudControllerBase<Note, UpsertNoteRequest, UpsertNoteRequest, INoteRepository>(repo)
{
    protected override Note MapCreateToModel(UpsertNoteRequest request) => new()
    {
        Timestamp = request.Timestamp.UtcDateTime,
        UtcOffset = request.UtcOffset,
        Device = request.Device,
        App = request.App,
        DataSource = request.DataSource,
        Text = request.Text ?? string.Empty,
        EventType = request.EventType,
        IsAnnouncement = request.IsAnnouncement,
        SyncIdentifier = request.SyncIdentifier,
    };

    protected override Note MapUpdateToModel(Guid id, UpsertNoteRequest request, Note existing) => new()
    {
        Id = id,
        Timestamp = request.Timestamp.UtcDateTime,
        UtcOffset = request.UtcOffset,
        Device = request.Device,
        App = request.App,
        DataSource = request.DataSource,
        Text = request.Text ?? string.Empty,
        EventType = request.EventType,
        IsAnnouncement = request.IsAnnouncement,
        CorrelationId = existing.CorrelationId,
        LegacyId = existing.LegacyId,
        CreatedAt = existing.CreatedAt,
        SyncIdentifier = existing.SyncIdentifier,
        AdditionalProperties = existing.AdditionalProperties,
    };

    /// <summary>
    /// Delete a note by its external sync identifier (dataSource + syncIdentifier pair).
    /// </summary>
    [HttpDelete("by-sync-id")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> DeleteBySyncIdentifier(
        [FromQuery] string dataSource,
        [FromQuery] string syncIdentifier,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(dataSource) || string.IsNullOrEmpty(syncIdentifier))
            return BadRequest("dataSource and syncIdentifier are required");

        var deleted = await ((INoteRepository)Repository).DeleteBySyncIdentifierAsync(dataSource, syncIdentifier, ct);
        return deleted > 0 ? NoContent() : NotFound();
    }
}

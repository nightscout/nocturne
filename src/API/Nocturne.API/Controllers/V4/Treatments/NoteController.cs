using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Models.Requests.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.API.Controllers.V4.Treatments;

/// <summary>
/// Notes about diabetes care. List, read, create (one or in bulk), update and delete notes. List
/// deleted notes and restore them. Delete a note by its data source and sync identifier.
/// </summary>
/// <remarks>
/// Standard V4 CRUD operations via <see cref="V4CrudControllerBase{TModel,TCreateRequest,TUpdateRequest,TRepository}"/>.
///
/// Create and update use the same <see cref="UpsertNoteRequest"/> shape.
/// On update, immutable fields (<see cref="V4RecordBase.CorrelationId"/>, <see cref="V4RecordBase.LegacyId"/>,
/// <see cref="V4RecordBase.CreatedAt"/>, <see cref="Note.SyncIdentifier"/>, and
/// <see cref="V4RecordBase.AdditionalProperties"/>) are preserved from the existing record.
/// </remarks>
/// <seealso cref="INoteRepository"/>
/// <seealso cref="Note"/>
/// <seealso cref="UpsertNoteRequest"/>
[ApiController]
[System.ComponentModel.DisplayName("Notes")]
[Tags("Treatments")]
[Route("api/v4/observations/notes")]
[RequireScope(Scope.TreatmentsRead)]
[Produces("application/json")]
public class NoteController(INoteRepository repo)
    : V4CrudControllerBase<Note, UpsertNoteRequest, UpsertNoteRequest, INoteRepository>(repo)
{
    /// <inheritdoc/>
    /// <remarks>Notes are the V4 form of a legacy text treatment (Note/Announcement/Question).</remarks>
    public override string WriteScope => Scope.TreatmentsReadWrite;

    /// <inheritdoc/>
    protected override V4BulkNaming BulkNaming => new("Note", "notes");

    protected override Note MapCreateToModel(UpsertNoteRequest request) => new Note
    {
        Text = request.Text ?? string.Empty,
        EventType = request.EventType,
        IsAnnouncement = request.IsAnnouncement,
        SyncIdentifier = request.SyncIdentifier,
    }.WithHeaderFrom(request);

    protected override Note MapUpdateToModel(Guid id, UpsertNoteRequest request, Note existing) => new Note
    {
        Id = id,
        Text = request.Text ?? string.Empty,
        EventType = request.EventType,
        IsAnnouncement = request.IsAnnouncement,
        CorrelationId = existing.CorrelationId,
        LegacyId = existing.LegacyId,
        CreatedAt = existing.CreatedAt,
        SyncIdentifier = existing.SyncIdentifier,
        AdditionalProperties = existing.AdditionalProperties,
    }.WithHeaderFrom(request);

    /// <summary>
    /// Delete a note by its external sync identifier (dataSource + syncIdentifier pair).
    /// </summary>
    [HttpDelete("by-sync-id")]
    [RequireDeclaredWriteScope]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> DeleteBySyncIdentifier(
        [FromQuery] string dataSource,
        [FromQuery] string syncIdentifier,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(dataSource) || string.IsNullOrEmpty(syncIdentifier))
            return BadRequest("dataSource and syncIdentifier are required");

        var deleted = await ((INoteRepository)Repository).DeleteBySyncIdentifierAsync(dataSource, syncIdentifier, WriteOrigin.Live, ct);
        return deleted > 0 ? NoContent() : NotFound();
    }
}

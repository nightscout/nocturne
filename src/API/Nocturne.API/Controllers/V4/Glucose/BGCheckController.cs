using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Models.Requests.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Controllers.V4.Glucose;

/// <summary>
/// Controller for managing blood glucose check observations recorded via a fingerstick or other
/// manual measurement method. Provides full CRUD operations backed by <see cref="IBGCheckRepository"/>.
/// </summary>
/// <remarks>
/// Inherits standard list, get-by-ID, create, update, and delete operations from
/// <see cref="V4CrudControllerBase{TModel,TCreateRequest,TUpdateRequest,TRepository}"/>.
/// Both create and update use the same <see cref="UpsertBGCheckRequest"/> shape.
/// The <c>SyncIdentifier</c> field on create is preserved verbatim; updates retain the
/// original value from the existing record.
/// </remarks>
/// <seealso cref="IBGCheckRepository"/>
/// <seealso cref="BGCheck"/>
/// <seealso cref="UpsertBGCheckRequest"/>
/// <seealso cref="V4CrudControllerBase{TModel,TCreateRequest,TUpdateRequest,TRepository}"/>
[ApiController]
[Tags("Glucose")]
[Route("api/v4/observations/bg-checks")]
[Authorize]
[Produces("application/json")]
public class BGCheckController(IBGCheckRepository repo)
    : V4CrudControllerBase<BGCheck, UpsertBGCheckRequest, UpsertBGCheckRequest, IBGCheckRepository>(repo)
{
    /// <summary>
    /// Maps a <see cref="UpsertBGCheckRequest"/> to a new <see cref="BGCheck"/> domain model for creation.
    /// </summary>
    /// <param name="request">The create request containing glucose value, units, and measurement metadata.</param>
    /// <returns>A new <see cref="BGCheck"/> instance ready for persistence.</returns>
    protected override BGCheck MapCreateToModel(UpsertBGCheckRequest request) => new()
    {
        Timestamp = request.Timestamp.UtcDateTime,
        UtcOffset = request.UtcOffset,
        Device = request.Device,
        App = request.App,
        DataSource = request.DataSource,
        Glucose = request.Glucose,
        Units = request.Units,
        GlucoseType = request.GlucoseType,
        SyncIdentifier = request.SyncIdentifier,
    };

    /// <summary>
    /// Maps a <see cref="UpsertBGCheckRequest"/> to an updated <see cref="BGCheck"/>, preserving
    /// immutable fields (<c>CorrelationId</c>, <c>LegacyId</c>, <c>CreatedAt</c>, <c>SyncIdentifier</c>,
    /// and <c>AdditionalProperties</c>) from the <paramref name="existing"/> record.
    /// </summary>
    /// <param name="id">The record ID being updated.</param>
    /// <param name="request">The update request.</param>
    /// <param name="existing">The existing record whose immutable fields are carried forward.</param>
    /// <returns>A <see cref="BGCheck"/> instance with updated mutable fields and preserved immutable fields.</returns>
    protected override BGCheck MapUpdateToModel(Guid id, UpsertBGCheckRequest request, BGCheck existing) => new()
    {
        Id = id,
        Timestamp = request.Timestamp.UtcDateTime,
        UtcOffset = request.UtcOffset,
        Device = request.Device,
        App = request.App,
        DataSource = request.DataSource,
        Glucose = request.Glucose,
        Units = request.Units,
        GlucoseType = request.GlucoseType,
        CorrelationId = existing.CorrelationId,
        LegacyId = existing.LegacyId,
        CreatedAt = existing.CreatedAt,
        SyncIdentifier = existing.SyncIdentifier,
        AdditionalProperties = existing.AdditionalProperties,
    };
}

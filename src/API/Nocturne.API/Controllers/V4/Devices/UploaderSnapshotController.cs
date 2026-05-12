using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4.Repositories;

namespace Nocturne.API.Controllers.V4.Devices;

/// <summary>
/// Controller for read-only access to uploader/bridge device snapshot data.
/// Exposes standard V4 read operations via <see cref="V4ReadOnlyControllerBase{TModel,TRepository}"/>.
/// </summary>
/// <remarks>
/// Uploader snapshots capture the state of the device running the upload software
/// (e.g. phone battery, connectivity status, app version). Records are written by
/// connector ingest pipelines and are not editable via the API.
/// </remarks>
/// <seealso cref="IUploaderSnapshotRepository"/>
/// <seealso cref="UploaderSnapshot"/>
[ApiController]
[Tags("Devices")]
[Route("api/v4/device-status/uploader")]
[Authorize]
[Produces("application/json")]
public class UploaderSnapshotController(IUploaderSnapshotRepository repo)
    : V4ReadOnlyControllerBase<UploaderSnapshot, IUploaderSnapshotRepository>(repo);

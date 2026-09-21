using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Attributes;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Controllers.V4.Analytics;

/// <summary>
/// Manually-entered lab HbA1c results (typically one or two a year), stored purely for comparison
/// against the computed eHbA1c estimate on the eHbA1c report. Not part of the V4 sync/dedup family:
/// these rows are typed in once, never uploaded by a device or connector, and never read by the
/// eHbA1c calculation itself.
/// </summary>
/// <seealso cref="ILabHbA1cResultRepository"/>
/// <seealso cref="LabHbA1cResult"/>
[ApiController]
[Tags("LabResults")]
[Route("api/v4/lab-results/hba1c")]
[RequireScope(Scope.GlucoseRead)]
[Produces("application/json")]
[ClientPropertyName("labHbA1c")]
public class LabHbA1cController : ControllerBase
{
    private readonly ILabHbA1cResultRepository _repository;

    /// <summary>Initializes a new instance of <see cref="LabHbA1cController"/>.</summary>
    /// <param name="repository">The lab HbA1c result store.</param>
    public LabHbA1cController(ILabHbA1cResultRepository repository)
    {
        _repository = repository;
    }

    /// <summary>Gets every saved lab HbA1c result for the current tenant, ordered by date.</summary>
    [HttpGet]
    [RemoteQuery]
    public async Task<ActionResult<IReadOnlyList<LabHbA1cResult>>> GetAll(
        CancellationToken cancellationToken = default
    )
    {
        var results = await _repository.GetAllAsync(cancellationToken);
        return Ok(results);
    }

    /// <summary>Records a new lab HbA1c result.</summary>
    [HttpPost]
    [RequireScope(Scope.GlucoseReadWrite)]
    [RemoteCommand(Invalidates = ["GetAll"])]
    public async Task<ActionResult<LabHbA1cResult>> Create(
        [FromBody] CreateLabHbA1cResultRequest request,
        CancellationToken cancellationToken = default
    )
    {
        if (request.ValuePercent is < 3.0 or > 20.0)
            return Problem(detail: "ValuePercent must be between 3.0 and 20.0", statusCode: 400, title: "Bad Request");
        if (request.MeasuredAt.Date > DateTime.UtcNow.Date.AddDays(1))
            return Problem(detail: "MeasuredAt cannot be in the future", statusCode: 400, title: "Bad Request");

        var created = await _repository.CreateAsync(
            new LabHbA1cResult
            {
                MeasuredAt = request.MeasuredAt.Date,
                ValuePercent = request.ValuePercent,
                Note = request.Note,
            },
            cancellationToken
        );
        return Ok(created);
    }

    /// <summary>Deletes a lab HbA1c result.</summary>
    [HttpDelete("{id:guid}")]
    [RequireScope(Scope.GlucoseReadWrite)]
    [RemoteCommand(Invalidates = ["GetAll"])]
    public async Task<ActionResult> Delete(Guid id, CancellationToken cancellationToken = default)
    {
        var deleted = await _repository.DeleteAsync(id, cancellationToken);
        if (!deleted)
            return NotFound();
        return NoContent();
    }
}

/// <summary>Request body for recording a lab HbA1c result.</summary>
public class CreateLabHbA1cResultRequest
{
    /// <summary>The date the blood was drawn.</summary>
    public DateTime MeasuredAt { get; set; }

    /// <summary>Lab-reported HbA1c in DCCT/NGSP percent (3.0-20.0).</summary>
    public double ValuePercent { get; set; }

    /// <summary>Optional free-text note (e.g. lab name).</summary>
    public string? Note { get; set; }
}

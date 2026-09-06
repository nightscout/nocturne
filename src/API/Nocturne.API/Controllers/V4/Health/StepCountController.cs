using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Models.Requests.V4;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using OpenApi.Remote.Attributes;

namespace Nocturne.API.Controllers.V4.Health;

/// <summary>
/// Controller for step count data from diabetes apps and wearables.
/// </summary>
/// <remarks>
/// Step count readings are stored as time-series observations. All operations delegate to
/// <see cref="IStepCountService"/>. Callers must hold the <c>read:health</c>
/// or <c>write:health</c> scope as appropriate.
/// </remarks>
/// <seealso cref="IStepCountService"/>
[ApiController]
[Tags("Health")]
[Route("api/v4/[controller]")]
[Produces("application/json")]
public class StepCountController(IStepCountService stepCountService)
    : HealthSeriesControllerBase<StepCount, UpsertStepCountRequest>
{
    protected override string RecordTypeName => "Step count";

    protected override Task<IEnumerable<StepCount>> ReadPageAsync(int count, int skip, CancellationToken ct) =>
        stepCountService.GetStepCountsAsync(count, skip, ct);

    protected override Task<IEnumerable<StepCount>> ReadRangeAsync(
        DateTime from, DateTime to, int count, int skip, CancellationToken ct) =>
        stepCountService.GetStepCountsByDateRangeAsync(from, to, count, skip, ct);

    protected override Task<StepCount?> ReadAsync(string id, CancellationToken ct) =>
        stepCountService.GetStepCountByIdAsync(id, ct);

    protected override Task<IEnumerable<StepCount>> WriteManyAsync(IReadOnlyList<StepCount> models, CancellationToken ct) =>
        stepCountService.CreateStepCountsAsync(models, ct);

    protected override Task<StepCount?> WriteAsync(string id, StepCount model, CancellationToken ct) =>
        stepCountService.UpdateStepCountAsync(id, model, ct);

    protected override Task<bool> EraseAsync(string id, CancellationToken ct) =>
        stepCountService.DeleteStepCountAsync(id, ct);

    protected override StepCount ToModel(UpsertStepCountRequest request) => new()
    {
        Timestamp = request.Timestamp.UtcDateTime,
        UtcOffset = request.UtcOffset,
        Metric = request.Metric,
        Source = request.Source,
        Device = request.Device,
        EnteredBy = request.App,
        DataSource = request.DataSource,
        SyncIdentifier = request.SyncIdentifier,
    };

    /// <summary>
    /// Get step count records with optional pagination and date filtering
    /// </summary>
    /// <param name="count">Maximum number of records to return (default: 10, or up to the ceiling when from/to are specified)</param>
    /// <param name="skip">Number of records to skip for pagination (default: 0)</param>
    /// <param name="from">Start of date range (inclusive).</param>
    /// <param name="to">End of date range (exclusive).</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of step count records</returns>
    /// <remarks>
    /// A date range without a <paramref name="count"/> reads up to
    /// <see cref="V4ReadLimits.MaxPageSize"/> records rather than the whole range, so a wide range
    /// cannot load the table into memory. Page through the rest with <paramref name="skip"/>.
    /// </remarks>
    [HttpGet]
    [RemoteQuery]
    [RequireScope(Scope.StepCountRead)]
    [ProducesResponseType(typeof(IEnumerable<StepCount>), 200)]
    [ProducesResponseType(500)]
    [ErrorEnvelope]
    public Task<ActionResult<IEnumerable<StepCount>>> GetStepCounts(
        [FromQuery] int? count = null,
        [FromQuery] int skip = 0,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        CancellationToken cancellationToken = default
    ) => ListResponseAsync(count, skip, from, to, cancellationToken);

    /// <summary>
    /// Get a specific step count record by ID
    /// </summary>
    /// <param name="id">Record ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    [HttpGet("{id}")]
    [RemoteQuery]
    [RequireScope(Scope.StepCountRead)]
    [ProducesResponseType(typeof(StepCount), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    [ErrorEnvelope]
    public Task<ActionResult<StepCount>> GetStepCount(
        string id,
        CancellationToken cancellationToken = default
    ) => GetResponseAsync(id, cancellationToken);

    /// <summary>
    /// Create one or more step count records
    /// </summary>
    [HttpPost]
    [RequireScope(Scope.StepCountReadWrite)]
    [ProducesResponseType(typeof(IEnumerable<StepCount>), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    [ErrorEnvelope]
    public Task<ActionResult<IEnumerable<StepCount>>> CreateStepCounts(
        [FromBody] UpsertStepCountRequest[] requests,
        CancellationToken cancellationToken = default
    ) => CreateResponseAsync(requests, cancellationToken);

    /// <summary>
    /// Update an existing step count record
    /// </summary>
    [HttpPut("{id}")]
    [RequireScope(Scope.StepCountReadWrite)]
    [ProducesResponseType(typeof(StepCount), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    [ErrorEnvelope]
    public Task<ActionResult<StepCount>> UpdateStepCount(
        string id,
        [FromBody] UpsertStepCountRequest request,
        CancellationToken cancellationToken = default
    ) => UpdateResponseAsync(id, ToModel(request), cancellationToken);

    /// <summary>
    /// Delete a step count record by ID
    /// </summary>
    [HttpDelete("{id}")]
    [RequireScope(Scope.StepCountReadWrite)]
    [ProducesResponseType(200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    [ErrorEnvelope]
    public Task<ActionResult> DeleteStepCount(
        string id,
        CancellationToken cancellationToken = default
    ) => DeleteResponseAsync(id, cancellationToken);
}

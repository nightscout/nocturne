using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Attributes;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Controllers.V4.Health;

/// <summary>
/// Controller for body weight tracking data.
/// </summary>
/// <remarks>
/// Provides time-series weight readings sourced from connected health apps.
/// All read and write operations delegate to <see cref="IBodyWeightService"/>.
/// </remarks>
/// <seealso cref="IBodyWeightService"/>
[ApiController]
[Tags("Health")]
[Route("api/v4/body-weight")]
[Authorize]
public class BodyWeightController(IBodyWeightService bodyWeightService)
    : HealthRecordControllerBase<BodyWeight>, IWriteScopedController
{
    /// <summary>
    /// The OAuth scope every write action on this controller requires. Body weight has no category
    /// scope of its own: the record is patient clinical configuration, written from the Patient
    /// Record settings form together with the therapy settings, so it is gated on
    /// <c>therapy.readwrite</c>. The <c>health.readwrite</c> alias cannot be required —
    /// <see cref="Scope.Normalize"/> expands it into per-category scopes, so no granted set
    /// ever contains it.
    /// </summary>
    public string WriteScope => Scope.TherapyReadWrite;

    protected override string RecordTypeName => "Body weight";

    protected override Task<IEnumerable<BodyWeight>> ReadPageAsync(int count, int skip, CancellationToken ct) =>
        bodyWeightService.GetBodyWeightsAsync(count, skip, ct);

    protected override Task<BodyWeight?> ReadAsync(string id, CancellationToken ct) =>
        bodyWeightService.GetBodyWeightByIdAsync(id, ct);

    protected override Task<IEnumerable<BodyWeight>> WriteManyAsync(IReadOnlyList<BodyWeight> models, CancellationToken ct) =>
        bodyWeightService.CreateBodyWeightsAsync(models, ct);

    protected override Task<BodyWeight?> WriteAsync(string id, BodyWeight model, CancellationToken ct) =>
        bodyWeightService.UpdateBodyWeightAsync(id, model, ct);

    protected override Task<bool> EraseAsync(string id, CancellationToken ct) =>
        bodyWeightService.DeleteBodyWeightAsync(id, ct);

    /// <summary>
    /// Get body weight records with optional pagination
    /// </summary>
    /// <param name="count">Maximum number of records to return (default: 10)</param>
    /// <param name="skip">Number of records to skip for pagination (default: 0)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of body weight records ordered by most recent first</returns>
    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(typeof(IEnumerable<BodyWeight>), 200)]
    [ProducesResponseType(500)]
    [ErrorEnvelope]
    public Task<ActionResult<IEnumerable<BodyWeight>>> GetBodyWeights(
        [FromQuery] int count = DefaultCount,
        [FromQuery] int skip = 0,
        CancellationToken cancellationToken = default
    ) => ListResponseAsync(count, skip, cancellationToken);

    /// <summary>
    /// Get a specific body weight record by ID
    /// </summary>
    /// <param name="id">Record ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    [HttpGet("{id}")]
    [RemoteQuery]
    [ProducesResponseType(typeof(BodyWeight), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    [ErrorEnvelope]
    public Task<ActionResult<BodyWeight>> GetBodyWeight(
        string id,
        CancellationToken cancellationToken = default
    ) => GetResponseAsync(id, cancellationToken);

    /// <summary>
    /// Create a single body weight record
    /// </summary>
    [HttpPost]
    [RequireDeclaredWriteScope]
    [RemoteCommand(Invalidates = ["GetBodyWeights"])]
    [ProducesResponseType(typeof(BodyWeight), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(500)]
    [ErrorEnvelope]
    public async Task<ActionResult<BodyWeight>> Create(
        [FromBody] BodyWeight bodyWeight,
        CancellationToken cancellationToken = default
    )
    {
        if (bodyWeight == null)
            return Problem(detail: "Body weight data is required", statusCode: 400, title: "Bad Request");

        var result = await WriteManyAsync([bodyWeight], cancellationToken);
        return StatusCode(StatusCodes.Status201Created, result.First());
    }

    /// <summary>
    /// Create one or more body weight records (single object or array)
    /// </summary>
    // Untyped so one route takes either a bare record or an array of them. Seven published SDKs
    // are generated from this operation, so the request shape cannot be tightened in place.
    [HttpPost("batch")]
    [RequireDeclaredWriteScope]
    [ProducesResponseType(typeof(IEnumerable<BodyWeight>), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    [ErrorEnvelope]
    public Task<ActionResult<IEnumerable<BodyWeight>>> CreateBodyWeights(
        [FromBody] object bodyWeights,
        CancellationToken cancellationToken = default
    )
    {
        if (bodyWeights is null)
            return Task.FromResult<ActionResult<IEnumerable<BodyWeight>>>(
                Problem(detail: "Body weight data is required", statusCode: 400, title: "Bad Request"));

        if (bodyWeights is not JsonElement json)
            return Task.FromResult<ActionResult<IEnumerable<BodyWeight>>>(
                Problem(detail: "Invalid data format", statusCode: 400, title: "Bad Request"));

        List<BodyWeight> models = json.ValueKind == JsonValueKind.Array
            ? JsonSerializer.Deserialize<List<BodyWeight>>(json.GetRawText()) ?? []
            : JsonSerializer.Deserialize<BodyWeight>(json.GetRawText()) is { } single ? [single] : [];

        return CreateResponseAsync(models, cancellationToken);
    }

    /// <summary>
    /// Update an existing body weight record
    /// </summary>
    [HttpPut("{id}")]
    [RequireDeclaredWriteScope]
    [RemoteCommand(Invalidates = ["GetBodyWeights", "GetBodyWeight"])]
    [ProducesResponseType(typeof(BodyWeight), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    [ErrorEnvelope]
    public Task<ActionResult<BodyWeight>> UpdateBodyWeight(
        string id,
        [FromBody] BodyWeight bodyWeight,
        CancellationToken cancellationToken = default
    ) => UpdateResponseAsync(id, bodyWeight, cancellationToken);

    /// <summary>
    /// Delete a body weight record by ID
    /// </summary>
    [HttpDelete("{id}")]
    [RequireDeclaredWriteScope]
    [RemoteCommand(Invalidates = ["GetBodyWeights", "GetBodyWeight"])]
    [ProducesResponseType(200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    [ErrorEnvelope]
    public Task<ActionResult> DeleteBodyWeight(
        string id,
        CancellationToken cancellationToken = default
    ) => DeleteResponseAsync(id, cancellationToken);
}

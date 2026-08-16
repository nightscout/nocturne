using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Nocturne.API.Extensions;
using Nocturne.API.Services;
using Nocturne.Core.Contracts.Content;
using Nocturne.Core.Models.Content;
using OpenApi.Remote.Attributes;

namespace Nocturne.API.Controllers.V4.Platform;

[ApiController]
[Authorize]
[Route("api/v4/content")]
public class ContentContributionsController(
    IContentContributionService contentService,
    ILogger<ContentContributionsController> logger) : ControllerBase
{
    internal const int MaxContentBytes = 512 * 1024;
    internal const int MaxTitleLength = 200;

    [HttpPost("contributions")]
    [RemoteCommand]
    [EnableRateLimiting(ServiceRegistrationExtensions.ContributionsRateLimitPolicy)]
    [ProducesResponseType(typeof(ContentContributionResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<ContentContributionResponse>> SubmitContribution(
        [FromBody] ContentContributionRequest request, CancellationToken ct)
    {
        var validationError = Validate(request);
        if (validationError is not null)
            return validationError;

        Func<Task<ContentContributionResponse>> submit = contentService.HasLocalPat
            ? () => contentService.SubmitAsync(request, ct)
            : () => contentService.RelayAsync(request, ct);

        return await SubmitAsync(submit, "content contribution");
    }

    /// <summary>
    /// Anonymous ingress for contributions relayed from instances or tools
    /// without their own PAT (the nocturne.run side of the relay).
    /// </summary>
    [HttpPost("relay")]
    [AllowAnonymous]
    [EnableRateLimiting(ServiceRegistrationExtensions.ContributionsRateLimitPolicy)]
    [ApiExplorerSettings(IgnoreApi = true)]
    public async Task<ActionResult<ContentContributionResponse>> AcceptRelayedContribution(
        [FromBody] ContentContributionRequest request, CancellationToken ct)
    {
        if (!contentService.AcceptsRelay)
            return NotFound();

        var validationError = Validate(request);
        if (validationError is not null)
            return validationError;

        return await SubmitAsync(
            () => contentService.SubmitAsync(request, ct), "relayed content contribution");
    }

    /// <summary>
    /// One error policy for both ingresses, so the direct and relayed paths
    /// cannot answer the same failure differently.
    /// </summary>
    private async Task<ActionResult<ContentContributionResponse>> SubmitAsync(
        Func<Task<ContentContributionResponse>> submit, string logContext)
    {
        try
        {
            return StatusCode(201, await submit());
        }
        catch (ContributionRejectedException ex)
        {
            return Problem(detail: ex.Message, statusCode: 422, title: "Unprocessable Entity");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to submit {LogContext}", logContext);
            return Problem(detail: "Failed to submit the contribution. Try again later.",
                statusCode: 502, title: "Bad Gateway");
        }
    }

    internal ObjectResult? Validate(ContentContributionRequest request)
    {
        // Bound the path independently of the allowlist: the value is echoed
        // into a branch name, a commit message and a PR body.
        if (request.Path.Length > ContributionValidation.MaxPathLength
            || !GitHubContentService.AllowedPathPattern().IsMatch(request.Path))
            return Problem(detail: "Path must be a portal blog or docs .svx file", statusCode: 400, title: "Bad Request");

        if (string.IsNullOrWhiteSpace(request.Content)
            || System.Text.Encoding.UTF8.GetByteCount(request.Content) > MaxContentBytes)
            return Problem(detail: "Content is required and must be under 512 KB", statusCode: 400, title: "Bad Request");

        if (string.IsNullOrWhiteSpace(request.Title)
            || request.Title.Length > MaxTitleLength
            || request.Title.Any(char.IsControl))
            return Problem(detail: $"Title is required, must be under {MaxTitleLength} characters, and cannot contain control characters", statusCode: 400, title: "Bad Request");

        return ContributionValidation.ValidateContributor(request.Contributor, request.Note) is { } reason
            ? Problem(detail: reason, statusCode: 400, title: "Bad Request")
            : null;
    }
}

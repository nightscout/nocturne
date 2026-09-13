using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Nocturne.API.Services;
using OpenApi.Remote.Attributes;

namespace Nocturne.API.Controllers.V4.Platform;

[ApiController]
[Authorize]
[Route("api/v4/translations")]
public partial class TranslationsController(
    GitHubTranslationService translationService,
    ILogger<TranslationsController> logger) : ControllerBase
{
    private const int MaxEntries = 500;
    private const int MaxMsgIdLength = 4096;
    private const int MaxTranslationLength = 8192;
    private const int MaxPluralForms = 8;

    private const int MaxContextLength = 256;

    // Anchored with \z, not $: in .NET $ also matches before a trailing
    // newline, and this value is interpolated into the catalog file path.
    [GeneratedRegex("^[a-z]{2,3}(-[A-Za-z0-9]{2,8})?\\z")]
    private static partial Regex LocalePattern();

    // GitHub's username grammar: alphanumeric and single hyphens, no
    // leading/trailing hyphen, max 39 chars.
    [GeneratedRegex("^[A-Za-z0-9](?:-?[A-Za-z0-9]){0,38}\\z")]
    private static partial Regex GitHubUsernamePattern();

    // Conservative mailbox shape: the value lands inside a Co-authored-by
    // trailer, so whitespace and angle brackets must be impossible.
    [GeneratedRegex(@"^[^\s<>@]+@[^\s<>@]+\.[^\s<>@]+\z")]
    private static partial Regex EmailPattern();

    [HttpPost("contributions")]
    [RemoteCommand]
    [EnableRateLimiting("translation-contributions")]
    [ProducesResponseType(typeof(TranslationContributionResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<TranslationContributionResponse>> SubmitContribution(
        [FromBody] TranslationContributionRequest request, CancellationToken ct)
    {
        var validationError = Validate(request);
        if (validationError is not null)
            return validationError;

        Func<Task<TranslationContributionResponse>> submit = translationService.HasLocalPat
            ? () => translationService.SubmitAsync(request, ct)
            : () => translationService.RelayAsync(request, ct);

        return await SubmitAsync(submit, "translation contribution");
    }

    /// <summary>
    /// Anonymous ingress for contributions relayed from instances without
    /// their own PAT (the nocturne.run side of the relay). The relayed payload
    /// is re-validated here; the rate limit is shared with the authenticated
    /// endpoint.
    /// </summary>
    [HttpPost("relay")]
    [AllowAnonymous]
    [EnableRateLimiting("translation-contributions")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public async Task<ActionResult<TranslationContributionResponse>> AcceptRelayedContribution(
        [FromBody] TranslationContributionRequest request, CancellationToken ct)
    {
        if (!translationService.AcceptsRelay)
            return NotFound();

        var validationError = Validate(request);
        if (validationError is not null)
            return validationError;

        return await SubmitAsync(
            () => translationService.SubmitAsync(request, ct), "relayed translation contribution");
    }

    /// <summary>
    /// One error policy for both ingresses, so the direct and relayed paths
    /// cannot answer the same failure differently.
    /// </summary>
    private async Task<ActionResult<TranslationContributionResponse>> SubmitAsync(
        Func<Task<TranslationContributionResponse>> submit, string logContext)
    {
        try
        {
            return StatusCode(201, await submit());
        }
        catch (TranslationContributionRejectedException ex)
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

    private static bool IsDisallowedControlChar(char c) =>
        char.IsControl(c) && c is not '\n' and not '\t' and not '\r';

    internal ObjectResult? Validate(TranslationContributionRequest request)
    {
        if (!LocalePattern().IsMatch(request.Locale))
            return Problem(detail: $"Invalid locale: {request.Locale}", statusCode: 400, title: "Bad Request");

        if (request.Entries.Count is 0 or > MaxEntries)
            return Problem(detail: $"Between 1 and {MaxEntries} entries required", statusCode: 400, title: "Bad Request");

        var seenKeys = new HashSet<(string, string)>();
        foreach (var entry in request.Entries)
        {
            if (string.IsNullOrEmpty(entry.MsgId) || entry.MsgId.Length > MaxMsgIdLength)
                return Problem(detail: "Each entry needs a msgid under 4096 characters", statusCode: 400, title: "Bad Request");
            if (entry.Context?.Length > MaxContextLength)
                return Problem(detail: "Entry context must be under 256 characters", statusCode: 400, title: "Bad Request");
            if (entry.Translations.Count is 0 or > MaxPluralForms
                || entry.Translations.Any(t => string.IsNullOrEmpty(t) || t.Length > MaxTranslationLength))
                return Problem(detail: "Each entry needs 1-8 non-empty translations under 8192 characters", statusCode: 400, title: "Bad Request");
            // Translation values are written verbatim into the committed .po
            // file, and the catalog escaper only handles \\ \" \n \t \r — any
            // other control character would land raw in the catalog.
            if (entry.Translations.Any(t => t.Any(IsDisallowedControlChar)))
                return Problem(detail: "Translations cannot contain control characters", statusCode: 400, title: "Bad Request");
            if (!seenKeys.Add((entry.Context ?? "", entry.MsgId)))
                return Problem(detail: "Duplicate entry for the same msgid and context", statusCode: 400, title: "Bad Request");
        }

        // Contributor identity ends up in the commit message (Co-authored-by
        // trailer) and PR body; control characters or trailer syntax in any
        // of these fields would allow commit-metadata injection.
        if (string.IsNullOrWhiteSpace(request.Contributor.Name)
            || request.Contributor.Name.Length > 128
            || request.Contributor.Name.Any(char.IsControl))
            return Problem(detail: "Contributor name is required, must be under 128 characters, and cannot contain control characters", statusCode: 400, title: "Bad Request");

        if (request.Contributor.GitHubUsername is { Length: > 0 } username
            && !GitHubUsernamePattern().IsMatch(username))
            return Problem(detail: "Invalid GitHub username", statusCode: 400, title: "Bad Request");

        if (request.Contributor.Email is { Length: > 0 } email
            && (email.Length > 254 || !EmailPattern().IsMatch(email)))
            return Problem(detail: "Invalid contributor email", statusCode: 400, title: "Bad Request");

        if (request.Note?.Length > 2000)
            return Problem(detail: "Note must be under 2000 characters", statusCode: 400, title: "Bad Request");

        return null;
    }
}

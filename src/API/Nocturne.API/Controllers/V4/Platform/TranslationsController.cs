using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Nocturne.API.Extensions;
using Nocturne.API.Services;
using Nocturne.Core.Contracts.Translations;
using Nocturne.Core.Models.Translations;
using OpenApi.Remote.Attributes;

namespace Nocturne.API.Controllers.V4.Platform;

public record UpsertTranslationDraftsRequest
{
    public required string Locale { get; init; }
    /// <summary>An entry with an empty Translations list deletes the draft.</summary>
    public required List<TranslationEntryDto> Entries { get; init; }
}

public record SubmitTranslationDraftsRequest
{
    public required string Locale { get; init; }
    public required TranslationContributorDto Contributor { get; init; }
    public string? Note { get; init; }
}

[ApiController]
[Authorize]
[Route("api/v4/translations")]
public partial class TranslationsController(
    ITranslationContributionService translationService,
    ITranslationDraftService draftService,
    ILogger<TranslationsController> logger) : ControllerBase
{
    // Large enough for a full-locale submission (the catalog is ~4.7k
    // messages) and matches the per-locale draft cap, so a drafts submit can
    // never exceed it.
    internal const int MaxEntries = 5000;

    // The relay keeps the original, tenfold lower bound. It is anonymous
    // ingress: every entry is regex-validated and then applied in a full
    // catalog rewrite, and nothing about a relayed contribution needs the
    // headroom a signed-in editor's full-locale drafts submit does.
    internal const int MaxRelayEntries = 500;

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
    /// is re-validated here, against the lower <see cref="MaxRelayEntries"/>
    /// ceiling; the rate limit is shared with the authenticated endpoint.
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

        var validationError = Validate(request, MaxRelayEntries);
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

    [HttpGet("drafts")]
    [RemoteQuery]
    [EnableRateLimiting(ServiceRegistrationExtensions.TranslationDraftsRateLimitPolicy)]
    [ProducesResponseType(typeof(IReadOnlyList<TranslationDraft>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<TranslationDraft>>> GetDrafts(
        [FromQuery] string locale, CancellationToken ct)
    {
        if (HttpContext.GetSubjectId() is null)
            return Unauthorized();
        if (!LocalePattern().IsMatch(locale))
            return Problem(detail: $"Invalid locale: {locale}", statusCode: 400, title: "Bad Request");

        return Ok(await draftService.GetDraftsAsync(locale, ct));
    }

    [HttpPut("drafts")]
    // No Invalidates: openapi-remote-codegen only threads path parameters into
    // an invalidation, and GetDrafts takes a required "locale" query parameter,
    // so the emitted refresh would call it with none and 400. The editor
    // refreshes its own per-locale query instance instead.
    [RemoteCommand]
    [EnableRateLimiting(ServiceRegistrationExtensions.TranslationDraftsRateLimitPolicy)]
    [ProducesResponseType(typeof(IReadOnlyList<TranslationDraft>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<TranslationDraft>>> UpsertDrafts(
        [FromBody] UpsertTranslationDraftsRequest request, CancellationToken ct)
    {
        if (HttpContext.GetSubjectId() is null)
            return Unauthorized();
        if (!LocalePattern().IsMatch(request.Locale))
            return Problem(detail: $"Invalid locale: {request.Locale}", statusCode: 400, title: "Bad Request");
        var entriesError = ValidateEntries(request.Entries, allowEmptyTranslations: true);
        if (entriesError is not null)
            return entriesError;

        try
        {
            return Ok(await draftService.UpsertDraftsAsync(request.Locale, request.Entries, ct));
        }
        catch (TranslationDraftLimitExceededException ex)
        {
            return Problem(detail: ex.Message, statusCode: 400, title: "Bad Request");
        }
    }

    [HttpDelete("drafts")]
    // No Invalidates; see UpsertDrafts.
    [RemoteCommand]
    [EnableRateLimiting(ServiceRegistrationExtensions.TranslationDraftsRateLimitPolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> ClearDrafts([FromQuery] string locale, CancellationToken ct)
    {
        if (HttpContext.GetSubjectId() is null)
            return Unauthorized();
        if (!LocalePattern().IsMatch(locale))
            return Problem(detail: $"Invalid locale: {locale}", statusCode: 400, title: "Bad Request");

        await draftService.ClearDraftsAsync(locale, ct);
        return NoContent();
    }

    [HttpPost("drafts/submit")]
    // No Invalidates; see UpsertDrafts.
    [RemoteCommand]
    [EnableRateLimiting("translation-contributions")]
    [ProducesResponseType(typeof(TranslationDraftSubmitResult), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<TranslationDraftSubmitResult>> SubmitDrafts(
        [FromBody] SubmitTranslationDraftsRequest request, CancellationToken ct)
    {
        if (HttpContext.GetSubjectId() is null)
            return Unauthorized();
        if (!LocalePattern().IsMatch(request.Locale))
            return Problem(detail: $"Invalid locale: {request.Locale}", statusCode: 400, title: "Bad Request");
        var contributorError = ValidateContributor(request.Contributor, request.Note);
        if (contributorError is not null)
            return contributorError;

        try
        {
            var result = await draftService.SubmitDraftsAsync(request.Locale, request.Contributor, request.Note, ct);
            return StatusCode(201, result);
        }
        catch (TranslationContributionRejectedException ex)
        {
            return Problem(detail: ex.Message, statusCode: 422, title: "Unprocessable Entity");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to submit translation drafts");
            return Problem(detail: "Failed to submit the contribution. Try again later.",
                statusCode: 502, title: "Bad Gateway");
        }
    }

    internal ObjectResult? Validate(TranslationContributionRequest request, int maxEntries = MaxEntries)
    {
        if (!LocalePattern().IsMatch(request.Locale))
            return Problem(detail: $"Invalid locale: {request.Locale}", statusCode: 400, title: "Bad Request");

        return ValidateEntries(request.Entries, allowEmptyTranslations: false, maxEntries)
            ?? ValidateContributor(request.Contributor, request.Note);
    }

    private ObjectResult? ValidateEntries(
        List<TranslationEntryDto> entries, bool allowEmptyTranslations, int maxEntries = MaxEntries)
    {
        if (entries.Count is 0 || entries.Count > maxEntries)
            return Problem(detail: $"Between 1 and {maxEntries} entries required", statusCode: 400, title: "Bad Request");

        var seenKeys = new HashSet<(string, string)>();
        foreach (var entry in entries)
        {
            if (string.IsNullOrEmpty(entry.MsgId) || entry.MsgId.Length > MaxMsgIdLength)
                return Problem(detail: "Each entry needs a msgid under 4096 characters", statusCode: 400, title: "Bad Request");
            if (entry.Context?.Length > MaxContextLength)
                return Problem(detail: "Entry context must be under 256 characters", statusCode: 400, title: "Bad Request");
            if ((entry.Translations.Count == 0 && !allowEmptyTranslations)
                || entry.Translations.Count > MaxPluralForms
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

        return null;
    }

    private ObjectResult? ValidateContributor(TranslationContributorDto contributor, string? note)
    {
        // Contributor identity ends up in the commit message (Co-authored-by
        // trailer) and PR body; control characters or trailer syntax in any
        // of these fields would allow commit-metadata injection.
        if (string.IsNullOrWhiteSpace(contributor.Name)
            || contributor.Name.Length > 128
            || contributor.Name.Any(char.IsControl))
            return Problem(detail: "Contributor name is required, must be under 128 characters, and cannot contain control characters", statusCode: 400, title: "Bad Request");

        if (contributor.GitHubUsername is { Length: > 0 } username
            && !GitHubUsernamePattern().IsMatch(username))
            return Problem(detail: "Invalid GitHub username", statusCode: 400, title: "Bad Request");

        if (contributor.Email is { Length: > 0 } email
            && (email.Length > 254 || !EmailPattern().IsMatch(email)))
            return Problem(detail: "Invalid contributor email", statusCode: 400, title: "Bad Request");

        if (note?.Length > 2000)
            return Problem(detail: "Note must be under 2000 characters", statusCode: 400, title: "Bad Request");

        return null;
    }
}

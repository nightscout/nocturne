using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
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
    public required ContributionContributorDto Contributor { get; init; }
    public string? Note { get; init; }
}

public record TranslationCatalogSourceResponse
{
    /// <summary>
    /// Directory the <c>{locale}.po</c> catalogs are read from, as raw files.
    /// No trailing slash.
    /// </summary>
    public required string CatalogBaseUrl { get; init; }
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

    [HttpPost("contributions")]
    [RemoteCommand]
    [EnableRateLimiting(ServiceRegistrationExtensions.ContributionsRateLimitPolicy)]
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

        return await SubmitAsync(submit, "translation contribution", ct);
    }

    /// <summary>
    /// Anonymous ingress for contributions relayed from instances without
    /// their own PAT (the nocturne.run side of the relay). The relayed payload
    /// is re-validated here, against the lower <see cref="MaxRelayEntries"/>
    /// ceiling; the rate limit is shared with the authenticated endpoint.
    /// </summary>
    [HttpPost("relay")]
    [AllowAnonymous]
    [EnableRateLimiting(ServiceRegistrationExtensions.ContributionsRateLimitPolicy)]
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
            () => translationService.SubmitAsync(request, ct), "relayed translation contribution", ct);
    }

    /// <summary>
    /// One error policy for both ingresses, so the direct and relayed paths
    /// cannot answer the same failure differently.
    /// </summary>
    private async Task<ActionResult<TranslationContributionResponse>> SubmitAsync(
        Func<Task<TranslationContributionResponse>> submit, string logContext, CancellationToken ct)
    {
        try
        {
            return StatusCode(201, await submit());
        }
        catch (ContributionRejectedException ex)
        {
            return Problem(detail: ex.Message, statusCode: 422, title: "Unprocessable Entity");
        }
        // A caller that went away is not an upstream failure, and answering it is pointless. The
        // guard is on the token rather than the exception type because an HttpClient timeout also
        // surfaces as OperationCanceledException, and that one is a gateway failure.
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        // The ways reaching GitHub (or the relay) fails: the request itself, the timeout, a
        // response the service rejects as unusable. Anything else is a defect and belongs to the
        // centralized handler rather than a 502 that reads like GitHub's fault.
        catch (Exception ex) when (ex is HttpRequestException
            or OperationCanceledException
            or JsonException
            or InvalidOperationException)
        {
            logger.LogError(ex, "Failed to submit {LogContext}", logContext);
            return Problem(detail: "Failed to submit the contribution. Try again later.",
                statusCode: 502, title: "Bad Gateway");
        }
    }

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
    [EnableRateLimiting(ServiceRegistrationExtensions.ContributionsRateLimitPolicy)]
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
        catch (ContributionRejectedException ex)
        {
            return Problem(detail: ex.Message, statusCode: 422, title: "Unprocessable Entity");
        }
        // Same policy as the contribution ingresses above, for the same reasons.
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException
            or OperationCanceledException
            or JsonException
            or InvalidOperationException)
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
            if (entry.Translations.Any(t => t.Any(ContributionValidation.IsDisallowedControlChar)))
                return Problem(detail: "Translations cannot contain control characters", statusCode: 400, title: "Bad Request");
            if (!seenKeys.Add((entry.Context ?? "", entry.MsgId)))
                return Problem(detail: "Duplicate entry for the same msgid and context", statusCode: 400, title: "Bad Request");
        }

        return null;
    }

    private ObjectResult? ValidateContributor(ContributionContributorDto contributor, string? note) =>
        ContributionValidation.ValidateContributor(contributor, note) is { } reason
            ? Problem(detail: reason, statusCode: 400, title: "Bad Request")
            : null;

    /// <summary>
    /// Where the editor must read the catalogs it drafts against.
    /// </summary>
    /// <remarks>
    /// Derived from the same <see cref="GitHubContributionOptions"/> the
    /// contribution writes through: an instance pointed at a fork or a test
    /// branch would otherwise have the editor drafting against upstream while
    /// the pull request lands elsewhere, and every entry would silently come
    /// back unmatched.
    /// </remarks>
    [HttpGet("catalog-source")]
    [RemoteQuery]
    [ProducesResponseType(typeof(TranslationCatalogSourceResponse), StatusCodes.Status200OK)]
    public ActionResult<TranslationCatalogSourceResponse> GetCatalogSource(
        [FromServices] IOptions<GitHubContributionOptions> githubOptions)
    {
        var opts = githubOptions.Value;
        var dir = EscapePath(opts.CatalogDir.Trim('/'));
        var baseUrl =
            $"https://raw.githubusercontent.com/{Uri.EscapeDataString(opts.Owner)}/{Uri.EscapeDataString(opts.Repo)}/{EscapePath(opts.BaseBranch)}"
            + (dir.Length == 0 ? "" : $"/{dir}");

        return Ok(new TranslationCatalogSourceResponse { CatalogBaseUrl = baseUrl });
    }

    /// <summary>
    /// Escapes each segment but keeps the separators: both a branch name and a
    /// catalog directory may legitimately contain slashes.
    /// </summary>
    private static string EscapePath(string value) =>
        string.Join('/', value.Split('/').Select(Uri.EscapeDataString));
}

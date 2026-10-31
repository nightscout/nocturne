using System.Text.Json;
using Microsoft.Extensions.Options;
using Nocturne.Core.Contracts.Audit;
using MvcJsonOptions = Microsoft.AspNetCore.Mvc.JsonOptions;

namespace Nocturne.API.Reports;

/// <summary>
/// Runs a spec the web app sends for the request's caller: validated, run under the caller's
/// scopes, history clamp and reader, and written to the read audit.
/// </summary>
public interface ICustomReportRunner
{
    Task<CustomReportRunResponse> RunAsync(CustomReportRunRequest request, CancellationToken cancellationToken);
}

internal sealed class CustomReportRunner(
    ReportSpecValidator validator,
    ReportRenderer renderer,
    IHttpContextAccessor httpContextAccessor,
    IServiceProvider services,
    ReportReadLog readLog,
    IAuditContext audit,
    IOptions<MvcJsonOptions> jsonOptions) : ICustomReportRunner
{
    /// <summary>The read-audit row's endpoint.</summary>
    public const string AuditEndpoint = "POST /api/v4/reports/custom/run";

    private JsonSerializerOptions Json => jsonOptions.Value.JsonSerializerOptions;

    public async Task<CustomReportRunResponse> RunAsync(CustomReportRunRequest request, CancellationToken cancellationToken)
    {
        if (request.Spec is not { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } spec)
            return await RefusedAsync(null, ReportSpecStore.SendASpec, cancellationToken);
        if (!ReportSpec.WithinLimit(spec))
            return await RefusedAsync(null, ReportSpec.TooLarge, cancellationToken);

        var validation = validator.Validate(
            ReportSpecRewriter.Current.Rewrite(spec), await renderer.UsesMmolAsync(cancellationToken));
        if (!validation.IsValid)
        {
            // An invalid spec read nothing: a 400 row with no categories.
            await AuditAsync(spec, StatusCodes.Status400BadRequest, cancellationToken);
            return Refused(spec, [.. ReportSpecStore.Errors(validation)]);
        }

        var normalised = JsonSerializer.SerializeToElement(validation.Spec, Json);
        var status = StatusCodes.Status500InternalServerError;
        try
        {
            var report = await renderer.RunAsync(validation.Spec!, cancellationToken);
            status = StatusCodes.Status200OK;
            return new CustomReportRunResponse(JsonSerializer.SerializeToElement(report, Json), normalised, []);
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
                await AuditAsync(normalised, status, cancellationToken);
        }
    }

    private static CustomReportRunResponse Refused(JsonElement? spec, params CustomReportSpecError[] errors) =>
        new(null, spec, errors);

    /// <summary>A run refused before the spec could be read: audited as a 400 that read nothing.</summary>
    private async Task<CustomReportRunResponse> RefusedAsync(JsonElement? spec, string message, CancellationToken cancellationToken)
    {
        await AuditAsync(spec, StatusCodes.Status400BadRequest, cancellationToken);
        return Refused(spec, new CustomReportSpecError("spec", message));
    }

    private Task AuditAsync(JsonElement? spec, int status, CancellationToken cancellationToken)
    {
        var http = httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException("A custom report ran outside an HTTP request.");
        var arguments = spec is { } sent ? ReportReadAudit.SpecArgument(sent) : null;
        return ReportReadAudit.WriteAsync(services, http, AuditEndpoint, tenantId => ReportReadAudit.BuildEntry(
            tenantId, AuditEndpoint, arguments, status, readLog, audit, http, DateTime.UtcNow), cancellationToken);
    }
}

using System.Text.Json;
using Microsoft.Extensions.Options;
using Nocturne.Core.Contracts.Audit;
using MvcJsonOptions = Microsoft.AspNetCore.Mvc.JsonOptions;

namespace Nocturne.API.Mcp.Reports;

/// <summary>
/// Runs a spec the web app sends for the request's caller: the same validator, runner, scopes,
/// history clamp and read audit as <c>render_report</c>, with the run's own audit endpoint.
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
    McpToolReadLog readLog,
    IAuditContext audit,
    IOptions<MvcJsonOptions> jsonOptions) : ICustomReportRunner
{
    /// <summary>The read-audit row's endpoint.</summary>
    public const string AuditEndpoint = "POST /api/v4/reports/custom/run";

    private JsonSerializerOptions Json => jsonOptions.Value.JsonSerializerOptions;

    public async Task<CustomReportRunResponse> RunAsync(CustomReportRunRequest request, CancellationToken cancellationToken)
    {
        JsonElement spec;
        var given = request.Spec is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } sent ? sent : (JsonElement?)null;
        if (request.Encoded is not null)
        {
            if (given is not null)
                return await RefusedAsync(null, "Send the spec or the encoded spec, not both.", cancellationToken);
            var read = ReportSpecLink.Decode(request.Encoded);
            if (read.Spec is not { } decoded)
                return await RefusedAsync(null, read.Error!, cancellationToken);
            spec = decoded;
        }
        else if (given is { } inline)
        {
            if (!ReportSpecLink.WithinLimit(inline))
                return await RefusedAsync(null, ReportSpecLink.TooLarge, cancellationToken);
            spec = inline;
        }
        else
        {
            return await RefusedAsync(null, ReportSpecStore.SendASpec, cancellationToken);
        }

        var validation = validator.Validate(
            ReportSpecRewriter.Current.Rewrite(spec), await renderer.UsesMmolAsync(cancellationToken));
        if (!validation.IsValid)
        {
            // Audited as render_report audits an invalid spec: a 400 that read nothing.
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

    /// <summary>A run refused before the spec could be read: audited as a 400 that read nothing, as <c>render_report</c> audits every call.</summary>
    private async Task<CustomReportRunResponse> RefusedAsync(JsonElement? spec, string message, CancellationToken cancellationToken)
    {
        await AuditAsync(spec, StatusCodes.Status400BadRequest, cancellationToken);
        return Refused(spec, new CustomReportSpecError("spec", message));
    }

    private Task AuditAsync(JsonElement? spec, int status, CancellationToken cancellationToken)
    {
        var http = httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException("A custom report ran outside an HTTP request.");
        var arguments = spec is { } sent
            ? McpToolGuard.SanitizeArguments(
                new Dictionary<string, JsonElement> { ["spec"] = sent }, new HashSet<string>(StringComparer.Ordinal) { "spec" })
            : null;
        return McpToolGuard.WriteReadAuditAsync(services, http, AuditEndpoint, tenantId => McpToolGuard.BuildReadEntry(
            tenantId, AuditEndpoint, arguments, status, readLog, audit, http, DateTime.UtcNow), cancellationToken);
    }
}

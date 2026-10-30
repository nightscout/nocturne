using System.Text.Json;
using Microsoft.Extensions.Options;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Reports;
using Nocturne.Core.Models.Reports;
using MvcJsonOptions = Microsoft.AspNetCore.Mvc.JsonOptions;

namespace Nocturne.API.Mcp.Reports;

/// <summary>
/// The tenant's saved custom reports. Every spec written goes through <see cref="ReportSpecValidator"/>
/// and is stored normalised; every spec read is rewritten to the current version
/// (<see cref="ReportSpecRewriter"/>) and validated again, so a report saved by an older version
/// opens, or says what is wrong with it.
/// </summary>
public interface IReportSpecStore
{
    Task<IReadOnlyList<CustomReportSummary>> ListAsync(CancellationToken cancellationToken);

    Task<CustomReport?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<CustomReportWrite> CreateAsync(string name, JsonElement spec, Guid? createdBySubjectId, CancellationToken cancellationToken);

    Task<CustomReportWrite> UpdateAsync(Guid id, string? name, JsonElement? spec, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);
}

internal sealed class ReportSpecStore(
    IReportSpecRepository repository,
    ReportSpecValidator validator,
    ITenantAppearanceReader appearance,
    ITenantAccessor tenant,
    IOptions<MvcJsonOptions> jsonOptions) : IReportSpecStore
{
    private JsonSerializerOptions Json => jsonOptions.Value.JsonSerializerOptions;

    public async Task<IReadOnlyList<CustomReportSummary>> ListAsync(CancellationToken cancellationToken) =>
        [.. (await repository.ListAsync(cancellationToken)).Select(r => new CustomReportSummary(r.Id, r.Name, r.UpdatedAt, PanelCount(r.SpecJson)))];

    public async Task<CustomReport?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await repository.GetAsync(id, cancellationToken) is not { } saved)
            return null;

        using var document = JsonDocument.Parse(saved.SpecJson);
        var stored = document.RootElement.Clone();
        var validation = validator.Validate(ReportSpecRewriter.Current.Rewrite(stored), await UsesMmolAsync(cancellationToken));
        return validation.IsValid
            ? ToReport(saved, JsonSerializer.SerializeToElement(validation.Spec, Json), [])
            : ToReport(saved, stored, Errors(validation));
    }

    public async Task<CustomReportWrite> CreateAsync(
        string name, JsonElement spec, Guid? createdBySubjectId, CancellationToken cancellationToken)
    {
        var errors = new List<CustomReportSpecError>();
        var trimmed = ReadName(name, errors);
        var normalised = await NormaliseAsync(spec, errors, cancellationToken);
        if (errors.Count > 0)
            return Invalid(errors);

        var write = await repository.CreateAsync(trimmed!, normalised!, ReportSpec.CurrentVersion, createdBySubjectId, cancellationToken);
        return ToWrite(write);
    }

    public async Task<CustomReportWrite> UpdateAsync(Guid id, string? name, JsonElement? spec, CancellationToken cancellationToken)
    {
        var errors = new List<CustomReportSpecError>();
        var trimmed = name is null ? null : ReadName(name, errors);
        var normalised = spec is { } given ? await NormaliseAsync(given, errors, cancellationToken) : null;
        if (errors.Count > 0)
            return Invalid(errors);

        var write = await repository.UpdateAsync(
            id, trimmed, normalised, normalised is null ? null : ReportSpec.CurrentVersion, cancellationToken);
        return ToWrite(write);
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
        repository.DeleteAsync(id, cancellationToken);

    /// <summary>The trimmed name, or null with the reason added to <paramref name="errors"/>.</summary>
    internal static string? ReadName(string? name, List<CustomReportSpecError> errors)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            errors.Add(new("name", "Enter a name."));
        else if (trimmed.Length > MaxNameLength)
            errors.Add(new("name", $"A name can have at most {MaxNameLength} characters."));
        else if (trimmed.Any(char.IsControl))
            errors.Add(new("name", "A name cannot have line breaks or control characters."));
        else
            return trimmed;
        return null;
    }

    public const int MaxNameLength = Infrastructure.Data.Entities.ReportSpecEntity.MaxNameLength;

    private async Task<string?> NormaliseAsync(JsonElement spec, List<CustomReportSpecError> errors, CancellationToken cancellationToken)
    {
        if (!ReportSpecLink.WithinLimit(spec))
        {
            errors.Add(new("spec", ReportSpecLink.TooLarge));
            return null;
        }

        var validation = validator.Validate(spec, await UsesMmolAsync(cancellationToken));
        if (!validation.IsValid)
        {
            errors.AddRange(Errors(validation));
            return null;
        }
        return JsonSerializer.Serialize(validation.Spec, Json);
    }

    private Task<bool> UsesMmolAsync(CancellationToken cancellationToken) =>
        GlucoseUnits.UsesMmolAsync(appearance, tenant, cancellationToken);

    internal static IReadOnlyList<CustomReportSpecError> Errors(ReportSpecValidation validation) =>
        [.. validation.Errors.Select(e => new CustomReportSpecError(e.Path, e.Message))];

    private static CustomReportWrite Invalid(IReadOnlyList<CustomReportSpecError> errors) =>
        new(CustomReportWriteStatus.Invalid, null, errors);

    private CustomReportWrite ToWrite(SavedReportSpecWrite write) => write.Status switch
    {
        SavedReportSpecWriteStatus.Saved => new(CustomReportWriteStatus.Saved, ToStoredReport(write.Report!), []),
        SavedReportSpecWriteStatus.NotFound => new(CustomReportWriteStatus.NotFound, null, []),
        SavedReportSpecWriteStatus.NameTaken => new(CustomReportWriteStatus.NameTaken, null,
            [new("name", "Another custom report has this name. Enter a different name.")]),
        _ => throw new ArgumentOutOfRangeException(nameof(write), write.Status, null),
    };

    /// <summary>A row just written: its spec is normalised and current, so it is returned as stored.</summary>
    private static CustomReport ToStoredReport(SavedReportSpec saved)
    {
        using var document = JsonDocument.Parse(saved.SpecJson);
        return ToReport(saved, document.RootElement.Clone(), []);
    }

    private static CustomReport ToReport(SavedReportSpec saved, JsonElement spec, IReadOnlyList<CustomReportSpecError> errors) =>
        new(saved.Id, saved.Name, saved.CreatedAt, saved.UpdatedAt, spec, errors);

    /// <summary>The panels a stored spec lists, without validating it; 0 when it lists none.</summary>
    internal static int PanelCount(string specJson)
    {
        try
        {
            using var document = JsonDocument.Parse(specJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("panels", out var panels)
                && panels.ValueKind == JsonValueKind.Array
                    ? panels.GetArrayLength()
                    : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }
}

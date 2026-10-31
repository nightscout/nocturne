using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Reports;
using Nocturne.Core.Models.Reports;
using MvcJsonOptions = Microsoft.AspNetCore.Mvc.JsonOptions;

namespace Nocturne.API.Reports;

/// <summary>
/// The tenant's saved custom reports. Every spec written goes through <see cref="ReportSpecValidator"/>
/// and is stored normalised; every spec read is rewritten to the current version
/// (<see cref="ReportSpecRewriter"/>) and validated again, so a report saved by an older version
/// opens, or says what is wrong with it. A rename, a new spec or a delete is refused for a caller
/// who neither created the report nor edits every report (<see cref="CustomReportEditor"/>).
/// </summary>
public interface IReportSpecStore
{
    Task<IReadOnlyList<CustomReportSummary>> ListAsync(CustomReportEditor caller, CancellationToken cancellationToken);

    Task<CustomReport?> GetAsync(Guid id, CustomReportEditor caller, CancellationToken cancellationToken);

    /// <summary>Saves a spec; refused once the tenant keeps <see cref="ReportSpecStore.MaxReports"/> live reports.</summary>
    Task<CustomReportWrite> CreateAsync(string name, JsonElement? spec, CustomReportEditor caller, CancellationToken cancellationToken);

    Task<CustomReportWrite> UpdateAsync(Guid id, string? name, JsonElement? spec, CustomReportEditor caller, CancellationToken cancellationToken);

    Task<CustomReportWriteStatus> DeleteAsync(Guid id, CustomReportEditor caller, CancellationToken cancellationToken);
}

internal sealed class ReportSpecStore(
    IReportSpecRepository repository,
    ReportSpecValidator validator,
    ITenantAppearanceReader appearance,
    ITenantAccessor tenant,
    IOptions<MvcJsonOptions> jsonOptions) : IReportSpecStore
{
    private JsonSerializerOptions Json => jsonOptions.Value.JsonSerializerOptions;

    /// <summary>The most live custom reports a tenant keeps.</summary>
    public const int MaxReports = 100;

    internal static readonly string TooMany =
        $"This account has {MaxReports} custom reports, the most it can keep. Delete a report to save another.";

    public async Task<IReadOnlyList<CustomReportSummary>> ListAsync(CustomReportEditor caller, CancellationToken cancellationToken) =>
        [.. (await repository.ListAsync(cancellationToken)).Select(r =>
            new CustomReportSummary(r.Id, r.Name, r.UpdatedAt, PanelCount(r.SpecJson), caller.CanEdit(r.CreatedBySubjectId)))];

    public async Task<CustomReport?> GetAsync(Guid id, CustomReportEditor caller, CancellationToken cancellationToken)
    {
        if (await repository.GetAsync(id, cancellationToken) is not { } saved)
            return null;

        using var document = JsonDocument.Parse(saved.SpecJson);
        var stored = document.RootElement.Clone();
        var validation = validator.Validate(ReportSpecRewriter.Current.Rewrite(stored), await UsesMmolAsync(cancellationToken));
        return validation.IsValid
            ? ToReport(saved, JsonSerializer.SerializeToElement(validation.Spec, Json), [], caller)
            : ToReport(saved, stored, Errors(validation), caller);
    }

    public async Task<CustomReportWrite> CreateAsync(
        string name, JsonElement? spec, CustomReportEditor caller, CancellationToken cancellationToken)
    {
        var errors = new List<CustomReportSpecError>();
        var trimmed = ReadName(name, errors);
        var normalised = await NormaliseAsync(spec, errors, cancellationToken);
        if (errors.Count > 0)
            return Invalid(errors);

        if (await repository.CountAsync(cancellationToken) >= MaxReports)
            return Invalid([new("name", TooMany)]);

        var write = await repository.CreateAsync(trimmed!, normalised!, ReportSpec.CurrentVersion, caller.SubjectId, cancellationToken);
        return ToWrite(write, caller);
    }

    public async Task<CustomReportWrite> UpdateAsync(
        Guid id, string? name, JsonElement? spec, CustomReportEditor caller, CancellationToken cancellationToken)
    {
        if (await RefusalAsync(id, caller, cancellationToken) is { } refused)
            return new CustomReportWrite(refused, null, []);

        var errors = new List<CustomReportSpecError>();
        var trimmed = name is null ? null : ReadName(name, errors);
        var normalised = spec is { } given ? await NormaliseAsync(given, errors, cancellationToken) : null;
        if (errors.Count > 0)
            return Invalid(errors);

        var write = await repository.UpdateAsync(
            id, trimmed, normalised, normalised is null ? null : ReportSpec.CurrentVersion, cancellationToken);
        return ToWrite(write, caller);
    }

    public async Task<CustomReportWriteStatus> DeleteAsync(Guid id, CustomReportEditor caller, CancellationToken cancellationToken) =>
        await RefusalAsync(id, caller, cancellationToken)
            ?? (await repository.DeleteAsync(id, cancellationToken) ? CustomReportWriteStatus.Saved : CustomReportWriteStatus.NotFound);

    /// <summary>Null when <paramref name="caller"/> may change the report; else why not.</summary>
    private async Task<CustomReportWriteStatus?> RefusalAsync(Guid id, CustomReportEditor caller, CancellationToken cancellationToken) =>
        await repository.GetAsync(id, cancellationToken) switch
        {
            null => CustomReportWriteStatus.NotFound,
            var saved when !caller.CanEdit(saved.CreatedBySubjectId) => CustomReportWriteStatus.Forbidden,
            _ => null,
        };

    /// <summary>The trimmed name, or null with the reason added to <paramref name="errors"/>.</summary>
    internal static string? ReadName(string? name, List<CustomReportSpecError> errors)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            errors.Add(new("name", "Enter a name."));
        else if (trimmed.Length > MaxNameLength)
            errors.Add(new("name", $"A name can have at most {MaxNameLength} characters."));
        else if (trimmed.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format))
            errors.Add(new("name", "A name cannot have line breaks, control characters or hidden format characters."));
        else
            return trimmed;
        return null;
    }

    internal const string SendASpec = "Send a spec.";

    public const int MaxNameLength = Infrastructure.Data.Entities.ReportSpecEntity.MaxNameLength;

    private async Task<string?> NormaliseAsync(JsonElement? given, List<CustomReportSpecError> errors, CancellationToken cancellationToken)
    {
        if (given is not { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } spec)
        {
            errors.Add(new("spec", SendASpec));
            return null;
        }
        if (!ReportSpec.WithinLimit(spec))
        {
            errors.Add(new("spec", ReportSpec.TooLarge));
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

    private static CustomReportWrite ToWrite(SavedReportSpecWrite write, CustomReportEditor caller) => write.Status switch
    {
        SavedReportSpecWriteStatus.Saved => new(CustomReportWriteStatus.Saved, ToStoredReport(write.Report!, caller), []),
        SavedReportSpecWriteStatus.NotFound => new(CustomReportWriteStatus.NotFound, null, []),
        SavedReportSpecWriteStatus.NameTaken => new(CustomReportWriteStatus.NameTaken, null,
            [new("name", "Another custom report has this name. Enter a different name.")]),
        _ => throw new ArgumentOutOfRangeException(nameof(write), write.Status, null),
    };

    /// <summary>A row just written: its spec is normalised and current, so it is returned as stored.</summary>
    private static CustomReport ToStoredReport(SavedReportSpec saved, CustomReportEditor caller)
    {
        using var document = JsonDocument.Parse(saved.SpecJson);
        return ToReport(saved, document.RootElement.Clone(), [], caller);
    }

    private static CustomReport ToReport(
        SavedReportSpec saved, JsonElement spec, IReadOnlyList<CustomReportSpecError> errors, CustomReportEditor caller) =>
        new(saved.Id, saved.Name, saved.CreatedAt, saved.UpdatedAt, spec, errors, caller.CanEdit(saved.CreatedBySubjectId));

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

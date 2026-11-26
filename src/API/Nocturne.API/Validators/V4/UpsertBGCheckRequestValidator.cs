using FluentValidation;
using Nocturne.API.Models.Requests.V4;

namespace Nocturne.API.Validators.V4;

/// <summary>
/// Validates <see cref="UpsertBGCheckRequest"/> for the V4 blood glucose check upsert endpoint.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>Timestamp must be a valid non-default <see cref="DateTimeOffset"/>.</description></item>
/// <item><description>The header rules of <see cref="RecordColumnRules"/>: caps on DataSource, SyncIdentifier, Device and App, and DataSource required when SyncIdentifier is supplied.</description></item>
/// <item><description>Glucose must be between 0 and 10,000 (mg/dL range guard).</description></item>
/// </list>
/// </remarks>
/// <seealso cref="UpsertBGCheckRequest"/>
/// <seealso cref="Controllers.V4.Glucose.BGCheckController"/>
public class UpsertBGCheckRequestValidator : AbstractValidator<UpsertBGCheckRequest>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UpsertBGCheckRequestValidator"/> class
    /// and configures all validation rules for BG check upserts.
    /// </summary>
    public UpsertBGCheckRequestValidator()
    {
        RuleFor(x => x.Timestamp).TimestampRequired();
        RuleFor(x => x.Device).WithinUploaderLabel();
        RuleFor(x => x.App).WithinUploaderLabel();
        RuleFor(x => x.DataSource).WithinProvenance();
        RuleFor(x => x.Glucose).InclusiveBetween(0, 10000).WithMessage("Glucose must be between 0 and 10000");
        RuleFor(x => x.SyncIdentifier).WithinSyncIdentifier();
        RuleFor(x => x.DataSource).DataSourceRequiredWithSyncIdentifier(x => x.SyncIdentifier);
    }
}

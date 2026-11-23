using FluentValidation;
using Nocturne.API.Models.Requests.V4;
using Nocturne.Core.Constants;

namespace Nocturne.API.Validators.V4;

/// <summary>
/// Validates <see cref="UpsertBGCheckRequest"/> for the V4 blood glucose check upsert endpoint.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>Timestamp must be a valid non-default <see cref="DateTimeOffset"/>.</description></item>
/// <item><description>DataSource capped at its column length (<see cref="RecordColumnLengths"/>); Device and App at 500.</description></item>
/// <item><description>Glucose must be between 0 and 10,000 (mg/dL range guard).</description></item>
/// <item><description>SyncIdentifier capped at its column length (<see cref="RecordColumnLengths"/>).</description></item>
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
        RuleFor(x => x.Device).MaximumLength(500).When(x => x.Device is not null);
        RuleFor(x => x.App).MaximumLength(500).When(x => x.App is not null);
        RuleFor(x => x.DataSource).MaximumLength(RecordColumnLengths.Provenance).When(x => x.DataSource is not null);
        RuleFor(x => x.Glucose).InclusiveBetween(0, 10000).WithMessage("Glucose must be between 0 and 10000");
        RuleFor(x => x.SyncIdentifier).MaximumLength(RecordColumnLengths.SyncIdentifier).When(x => x.SyncIdentifier is not null);
    }
}

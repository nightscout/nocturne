using FluentValidation;
using Nocturne.Core.Constants;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Validators.V4;

/// <summary>
/// Validates the <see cref="PatientRecord"/> body: each string at the length of the column it is stored in.
/// </summary>
public class PatientRecordValidator : AbstractValidator<PatientRecord>
{
    public PatientRecordValidator()
    {
        RuleFor(x => x.DiabetesTypeOther).MaximumLength(ColumnLengths.PatientRecord.DiabetesTypeOther);
        RuleFor(x => x.PreferredName).MaximumLength(ColumnLengths.PatientRecord.PreferredName);
        RuleFor(x => x.Pronouns).MaximumLength(ColumnLengths.PatientRecord.Pronouns);
        RuleFor(x => x.AvatarUrl).MaximumLength(ColumnLengths.PatientRecord.AvatarUrl);
        RuleFor(x => x.Timezone).MaximumLength(ColumnLengths.PatientRecord.Timezone);
    }
}

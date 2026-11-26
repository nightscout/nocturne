using FluentValidation;
using Nocturne.Core.Constants;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Validators.V4;

/// <summary>
/// Validates the <see cref="PatientInsulin"/> body: each string at the length of the column it is stored in.
/// </summary>
public class PatientInsulinValidator : AbstractValidator<PatientInsulin>
{
    public PatientInsulinValidator()
    {
        RuleFor(x => x.Name).MaximumLength(ColumnLengths.PatientInsulin.Name);
        RuleFor(x => x.Notes).MaximumLength(ColumnLengths.PatientInsulin.Notes);
        RuleFor(x => x.FormulationId).MaximumLength(ColumnLengths.PatientInsulin.FormulationId);
        RuleFor(x => x.Curve).MaximumLength(ColumnLengths.PatientInsulin.Curve);
    }
}

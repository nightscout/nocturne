using FluentValidation;
using Nocturne.Core.Constants;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Validators.V4;

/// <summary>
/// Validates the <see cref="PatientDevice"/> body: each string at the length of the column it is stored in.
/// </summary>
public class PatientDeviceValidator : AbstractValidator<PatientDevice>
{
    public PatientDeviceValidator()
    {
        RuleFor(x => x.Manufacturer).MaximumLength(ColumnLengths.PatientDevice.Manufacturer);
        RuleFor(x => x.Model).MaximumLength(ColumnLengths.PatientDevice.Model);
        RuleFor(x => x.CatalogId).MaximumLength(ColumnLengths.PatientDevice.CatalogId);
        RuleFor(x => x.SerialNumber).MaximumLength(ColumnLengths.PatientDevice.SerialNumber);
        RuleFor(x => x.Notes).MaximumLength(ColumnLengths.PatientDevice.Notes);
    }
}

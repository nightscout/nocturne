using FluentValidation;
using Nocturne.Core.Constants;
using Nocturne.Core.Models.Identity;

namespace Nocturne.API.Validators.V4;

/// <summary>
/// The checks of a fact compared with a value (<see cref="IFlagFactFields"/>), a flag rule's or a filter's: the dataset
/// and field at the length of their columns, a comparison and a finite value. Whether the fact exists, and takes the
/// range sent, is checked against the reports engine (<see cref="Reports.IFlagRuleCatalogue.Check"/>).
/// </summary>
/// <remarks>
/// Added to the validator of the type that carries the fact rather than run as a validator of its own, so each length
/// rule is the carrying type's own and the column-cap guard reads it there.
/// </remarks>
public static class FlagFactFieldRules
{
    /// <summary>Adds the fact's checks to <paramref name="validator"/>, under any condition it is building.</summary>
    public static void AddFactRules<T>(this AbstractValidator<T> validator)
        where T : IFlagFactFields
    {
        validator.RuleFor(x => x.Dataset).NotEmpty().MaximumLength(ColumnLengths.FlagRule.Dataset);
        validator.RuleFor(x => x.Field).NotEmpty().MaximumLength(ColumnLengths.FlagRule.Field);
        validator.RuleFor(x => x.Comparison).NotNull().WithMessage("Send a comparison: below or above.").IsInEnum();
        validator.RuleFor(x => x.Value).NotNull().WithMessage("Send a number.")
            .Must(value => value is null || double.IsFinite(value.Value)).WithMessage("Send a number.");
    }
}

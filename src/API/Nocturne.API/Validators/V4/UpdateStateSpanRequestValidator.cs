using FluentValidation;
using Nocturne.API.Controllers.V4.Analytics;

namespace Nocturne.API.Validators.V4;

/// <summary>
/// Validates <see cref="UpdateStateSpanRequest"/> for the V4 state span endpoint, capping State and Source
/// at the state span columns they are stored in.
/// </summary>
/// <seealso cref="StateSpansController"/>
public class UpdateStateSpanRequestValidator : AbstractValidator<UpdateStateSpanRequest>
{
    public UpdateStateSpanRequestValidator()
    {
        RuleFor(x => x.State).MaximumLength(100);
        RuleFor(x => x.Source).MaximumLength(50);
    }
}

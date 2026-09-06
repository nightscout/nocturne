using FluentValidation;
using Nocturne.API.Controllers.Authentication;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Validators.Auth;

/// <summary>
/// Validates <see cref="CreateDirectGrantRequest"/> for creating a direct (non-OAuth) access token grant.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>Label is required and capped at 200 characters.</description></item>
/// <item><description>At least one scope is required.</description></item>
/// <item><description>Each scope must be a value recognized by <see cref="Scope.IsValid"/>.</description></item>
/// <item><description>ExpiresAt is optional, and when set must be in the future: a grant issued
/// at or past its expiry authenticates nothing.</description></item>
/// </list>
/// </remarks>
/// <seealso cref="CreateDirectGrantRequest"/>
/// <seealso cref="DirectGrantController"/>
public class CreateDirectGrantRequestValidator : AbstractValidator<CreateDirectGrantRequest>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CreateDirectGrantRequestValidator"/> class
    /// and configures all validation rules for direct grant creation.
    /// </summary>
    public CreateDirectGrantRequestValidator()
    {
        RuleFor(x => x.Label).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Scopes).NotEmpty().WithMessage("At least one scope is required");
        RuleForEach(x => x.Scopes).Must(Scope.IsValid)
            .WithMessage(scope => $"Invalid scope: {scope}");
        RuleFor(x => x.ExpiresAt).Must(expiresAt => expiresAt > DateTime.UtcNow)
            .When(x => x.ExpiresAt.HasValue)
            .WithMessage("ExpiresAt must be in the future");
    }
}

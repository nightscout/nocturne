using FluentValidation;
using Nocturne.API.Controllers.Authentication;

namespace Nocturne.API.Validators.Auth;

/// <summary>
/// Validates <see cref="AccessRequestCompleteRequest"/> for the passkey-based access request completion step.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>DisplayName is required and capped at 255 characters — it resolves the pending subject.</description></item>
/// <item><description>AttestationResponseJson must be non-empty (contains the WebAuthn attestation response).</description></item>
/// <item><description>ChallengeToken must be non-empty (ties the response back to the server-issued challenge).</description></item>
/// </list>
/// </remarks>
/// <seealso cref="AccessRequestCompleteRequest"/>
/// <seealso cref="PasskeyController"/>
public class AccessRequestCompleteRequestValidator : AbstractValidator<AccessRequestCompleteRequest>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AccessRequestCompleteRequestValidator"/> class
    /// and configures all validation rules for access request completion.
    /// </summary>
    public AccessRequestCompleteRequestValidator()
    {
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(255);
        RuleFor(x => x.AttestationResponseJson).NotEmpty();
        RuleFor(x => x.ChallengeToken).NotEmpty();
    }
}

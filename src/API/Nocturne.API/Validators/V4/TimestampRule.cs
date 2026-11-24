using FluentValidation;
using Microsoft.AspNetCore.Mvc;

namespace Nocturne.API.Validators.V4;

/// <summary>
/// The one rule that a V4 record carries a timestamp, and the one message that says so.
/// </summary>
/// <remarks>
/// A request body that omits the timestamp binds to the default value, so default is what "missing" means.
/// </remarks>
public static class TimestampRule
{
    /// <summary>The message every V4 endpoint answers a missing timestamp with.</summary>
    public const string Message = "Timestamp is required";

    /// <summary>Whether the timestamp was left unset.</summary>
    public static bool IsMissing(DateTimeOffset timestamp) => timestamp == default;

    /// <inheritdoc cref="IsMissing(DateTimeOffset)"/>
    public static bool IsMissing(DateTime timestamp) => timestamp == default;

    /// <summary>Adds the rule to a validator.</summary>
    public static IRuleBuilderOptions<T, DateTimeOffset> TimestampRequired<T>(this IRuleBuilder<T, DateTimeOffset> rule)
        => rule.Must(timestamp => !IsMissing(timestamp)).WithMessage(Message);

    /// <summary>A 400 whose detail is <see cref="Message"/>, for a check an action makes itself.</summary>
    public static ObjectResult TimestampRequiredProblem(this ControllerBase controller)
        => controller.Problem(detail: Message, statusCode: 400, title: "Bad Request");
}

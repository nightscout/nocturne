namespace Nocturne.API.Attributes;

/// <summary>
/// Marks an action whose unhandled exceptions are shaped by
/// <see cref="Middleware.ApiErrorEnvelopeHandler"/> into the error envelope its API version uses.
/// </summary>
/// <remarks>
/// Opt-in per action: an action without it keeps the framework's default
/// <c>ProblemDetails</c> response.
/// </remarks>
/// <seealso cref="Middleware.ApiErrorEnvelopeHandler"/>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class ErrorEnvelopeAttribute : Attribute;

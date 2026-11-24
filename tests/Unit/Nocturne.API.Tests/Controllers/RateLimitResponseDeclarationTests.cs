using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Controllers.V4.Analytics;
using Nocturne.API.RateLimiting;

namespace Nocturne.API.Tests.Controllers;

/// <summary>
/// An action that declares a 429 body schema other than <see cref="ProblemDetails"/> makes the generated
/// client throw the bare parsed body, so the web cannot read the status. Every refusal the API writes
/// for a 429 is a problem document (<see cref="RateLimitRejection"/>), so a declared 429 is either that
/// or declares no body.
/// </summary>
public class RateLimitResponseDeclarationTests
{
    [Fact]
    public void EveryDeclared429_IsAProblemDocumentOrNoBody()
    {
        var offenders = Offenders(typeof(ChartDataController).Assembly.GetTypes());

        offenders.Should().BeEmpty(
            "a 429 declared with another schema hides its status from the web; declare ProblemDetails or typeof(void)");
    }

    [Fact]
    public void TheGuard_FlagsAnActionThatDeclaresAnotherSchema()
    {
        Offenders([typeof(TypedBodyController), typeof(TypedBodyOnTheClassController)])
            .Should().BeEquivalentTo(
                $"{nameof(TypedBodyController)}.{nameof(TypedBodyController.Typed)} declares {nameof(Rejection)}",
                $"{nameof(TypedBodyOnTheClassController)}.{nameof(TypedBodyOnTheClassController.Inherits)} declares {nameof(Rejection)}");
    }

    [Fact]
    public void TheGuard_PassesProblemDetailsNoBodyAndUntypedDeclarations()
    {
        Offenders([typeof(ConformingController)]).Should().BeEmpty();
    }

    private static List<string> Offenders(IEnumerable<Type> types) =>
        types
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.DeclaringType != typeof(ControllerBase) && m.DeclaringType != typeof(object))
                .SelectMany(m => m.GetCustomAttributes<ProducesResponseTypeAttribute>(inherit: true)
                    .Concat(t.GetCustomAttributes<ProducesResponseTypeAttribute>(inherit: true))
                    .Where(a => a.StatusCode == StatusCodes.Status429TooManyRequests)
                    .Where(a => a.Type != typeof(void) && !typeof(ProblemDetails).IsAssignableFrom(a.Type))
                    .Select(a => $"{t.Name}.{m.Name} declares {a.Type.Name}")))
            .Distinct()
            .ToList();

    private sealed record Rejection(string Error);

    private sealed class TypedBodyController : ControllerBase
    {
        [ProducesResponseType(typeof(Rejection), StatusCodes.Status429TooManyRequests)]
        public IActionResult Typed() => Ok();
    }

    [ProducesResponseType(typeof(Rejection), StatusCodes.Status429TooManyRequests)]
    private sealed class TypedBodyOnTheClassController : ControllerBase
    {
        public IActionResult Inherits() => Ok();
    }

    private sealed class ConformingController : ControllerBase
    {
        [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
        public IActionResult Problem429() => Ok();

        [ProducesResponseType(typeof(void), StatusCodes.Status429TooManyRequests)]
        public IActionResult NoBody() => Ok();

        [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
        public IActionResult Untyped() => Ok();

        [ProducesResponseType(typeof(Rejection), StatusCodes.Status400BadRequest)]
        public IActionResult OtherStatus() => Ok();
    }
}

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Services.Identity;

namespace Nocturne.API.Tests.Infrastructure;

/// <summary>The refusal a caller who holds no direct reason reads at a site that gives to others.</summary>
internal static class GrantCeilingAssertions
{
    public static void ShouldRefuseAsAccessFromTeam(IActionResult result)
    {
        var problem = result.Should().BeAssignableTo<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        var details = problem.Value.Should().BeOfType<ProblemDetails>().Subject;
        details.Extensions["code"].Should().Be(CallerAccessFromTeam.Code);
        details.Detail.Should().Be(CallerAccessFromTeam.Detail);
    }

    public static void ShouldRefuseAsAccessFromTeam<T>(ActionResult<T> result) =>
        ShouldRefuseAsAccessFromTeam(result.Result!);
}

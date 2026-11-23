using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Authorization;

namespace Nocturne.API.Tests.Infrastructure;

internal static class ScopeRefusalAssertions
{
    /// <summary>
    /// Asserts <paramref name="result"/> is the 403 that names <paramref name="scopes"/>, the refusal
    /// a caller missing them reads on a V4 route.
    /// </summary>
    public static ScopeForbiddenResult ShouldBeScopeRefusal(this IActionResult? result, params string[] scopes)
    {
        var refusal = result.Should().BeOfType<ScopeForbiddenResult>().Subject;
        refusal.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        refusal.Scopes.Should().Equal(scopes);
        return refusal;
    }
}

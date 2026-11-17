using System.Net;
using Nocturne.API.Tests.Infrastructure;

namespace Nocturne.API.Tests.Middleware;

/// <summary>
/// The headers <see cref="Nocturne.API.Extensions.SecurityHeadersExtensions"/> adds, over the real
/// <c>Program.cs</c> pipeline: a success and a refusal both carry them.
/// </summary>
public class SecurityHeadersPipelineTests(AuthenticationTestFactory factory)
    : IClassFixture<AuthenticationTestFactory>
{
    [Theory]
    [InlineData("/alive", HttpStatusCode.OK)]
    [InlineData("/api/v4/me/tenants/validate-slug?slug=x", HttpStatusCode.Unauthorized)]
    public async Task EveryResponseCarriesTheHeaders(string path, HttpStatusCode status)
    {
        var response = await factory.CreateClient().GetAsync(path);

        response.StatusCode.Should().Be(status);
        response.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
        response.Headers.GetValues("X-Frame-Options").Should().Equal("DENY");
        response.Headers.GetValues("Referrer-Policy").Should().Equal("strict-origin-when-cross-origin");
    }
}

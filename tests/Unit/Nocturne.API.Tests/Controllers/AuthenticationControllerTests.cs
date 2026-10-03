using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Controllers.V1;
using Nocturne.API.Extensions;
using Nocturne.API.Tests.Infrastructure;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Controllers;

public class AuthenticationControllerTests : IClassFixture<AuthenticationTestFactory>
{
    private static readonly string[] NightscoutMessageFields =
        ["canRead", "canWrite", "isAdmin", "message", "rolefound", "permissions"];

    private readonly AuthenticationTestFactory _factory;

    public AuthenticationControllerTests(AuthenticationTestFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task VerifyAuth_ValidApiSecret_ReportsFullAccessInNightscoutShape()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            "api-secret", TestDatabaseSeeder.Sha1Hex(AuthenticationTestFactory.ApiSecret));

        var body = await GetVerifyAuthAsync(client);

        body.GetProperty("status").GetInt32().Should().Be(200);
        var message = body.GetProperty("message");
        PropertyNames(message).Should().BeEquivalentTo(NightscoutMessageFields);
        message.GetProperty("canRead").GetBoolean().Should().BeTrue();
        message.GetProperty("canWrite").GetBoolean().Should().BeTrue();
        message.GetProperty("isAdmin").GetBoolean().Should().BeTrue();
        message.GetProperty("message").GetString().Should().Be("OK");
        message.GetProperty("rolefound").GetString().Should().Be("NOTFOUND");
        message.GetProperty("permissions").GetString().Should().Be("ROLE");
    }

    [Fact]
    public async Task VerifyAuth_NoCredential_AnswersUnauthorizedWithDefaultPermissions()
    {
        var body = await GetVerifyAuthAsync(_factory.CreateClient());

        AssertUnauthorized(body);
    }

    [Fact]
    public async Task VerifyAuth_WrongApiSecret_AnswersUnauthorized()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("api-secret", TestDatabaseSeeder.Sha1Hex("wrong-secret"));

        AssertUnauthorized(await GetVerifyAuthAsync(client));
    }

    [Fact]
    public async Task VerifyAuth_InvalidBearerToken_AnswersUnauthorized()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-token");

        AssertUnauthorized(await GetVerifyAuthAsync(client));
    }

    [Fact]
    public void VerifyAuth_ReadOnlyToken_CanReadButNotWriteOrAdminister()
    {
        var message = Verify(AuthType.DirectGrant, ScopeTranslator.FromPermissions(["*:*:read"]));

        message.CanRead.Should().BeTrue();
        message.CanWrite.Should().BeFalse();
        message.IsAdmin.Should().BeFalse();
        message.Message.Should().Be("OK");
        message.RoleFound.Should().Be("FOUND");
        message.Permissions.Should().Be("ROLE");
    }

    [Fact]
    public void VerifyAuth_TokenCoveringSomeCategories_CannotReadEverything()
    {
        var message = Verify(AuthType.DirectGrant, new HashSet<string> { Scope.GlucoseReadWrite });

        message.CanRead.Should().BeFalse();
        message.CanWrite.Should().BeFalse();
        message.IsAdmin.Should().BeFalse();
    }

    [Fact]
    public void VerifyAuth_WriteEverythingWithoutFullAccess_IsNotAdmin()
    {
        var message = Verify(AuthType.DirectGrant, ScopeTranslator.FromPermissions(["api:*:create", "*:*:read"]));

        message.CanRead.Should().BeTrue();
        message.CanWrite.Should().BeTrue();
        message.IsAdmin.Should().BeFalse();
    }

    private static VerifyAuthMessage Verify(AuthType authType, IReadOnlySet<string> scopes)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.SetAuthContext(new AuthContext { IsAuthenticated = true, AuthType = authType });
        httpContext.SetGrantedScopes(scopes);

        var controller = new AuthenticationController
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };

        var ok = controller.VerifyAuthentication().Result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<VerifyAuthResponse>().Subject;
        response.Status.Should().Be(200);
        return response.Message;
    }

    private static void AssertUnauthorized(JsonElement body)
    {
        body.GetProperty("status").GetInt32().Should().Be(200);
        var message = body.GetProperty("message");
        PropertyNames(message).Should().BeEquivalentTo(NightscoutMessageFields);
        message.GetProperty("message").GetString().Should().Be("UNAUTHORIZED");
        message.GetProperty("rolefound").GetString().Should().Be("NOTFOUND");
        message.GetProperty("permissions").GetString().Should().Be("DEFAULT");
        message.GetProperty("canWrite").GetBoolean().Should().BeFalse();
        message.GetProperty("isAdmin").GetBoolean().Should().BeFalse();
    }

    private static async Task<JsonElement> GetVerifyAuthAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/v1/verifyauth", CancellationToken.None);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync(CancellationToken.None);
        return JsonDocument.Parse(content).RootElement.Clone();
    }

    private static IEnumerable<string> PropertyNames(JsonElement element) =>
        element.EnumerateObject().Select(p => p.Name);
}

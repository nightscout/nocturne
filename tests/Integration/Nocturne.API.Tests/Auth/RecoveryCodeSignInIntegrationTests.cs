using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// A recovery code signs in only an active member. A deactivated one is answered as a username
/// the tenant does not have, and keeps the code.
/// </summary>
[Collection(ApiIntegrationShards.Three)]
[Trait("Category", "Integration")]
public class RecoveryCodeSignInIntegrationTests : ApiIntegrationTestBase
{
    public RecoveryCodeSignInIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    [Fact]
    public async Task A_deactivated_member_is_refused_as_an_unknown_username_and_keeps_the_code()
    {
        await using var conn = await OpenPostgresAsync();
        var tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);

        var (memberId, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantId, "Deactivated Member");
        var username = $"deactivated-{Guid.NewGuid():N}";
        await conn.ExecuteAsync("UPDATE subjects SET username = @username WHERE id = @id;",
            ("username", username), ("id", memberId));

        List<string> codes;
        using (var scope = Fixture.Services.CreateScope())
            codes = await scope.ServiceProvider.GetRequiredService<IRecoveryCodeService>().GenerateCodesAsync(memberId);

        await conn.ExecuteAsync("UPDATE subjects SET is_active = false WHERE id = @id;", ("id", memberId));

        try
        {
            using var client = Fixture.CreateHttpClient(ServiceNames.NocturneApi);

            var unknown = await client.PostAsJsonAsync("/api/auth/passkey/recovery/verify",
                new { username = $"nobody-{Guid.NewGuid():N}", code = codes[0] });
            var deactivated = await client.PostAsJsonAsync("/api/auth/passkey/recovery/verify",
                new { username, code = codes[0] });

            deactivated.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            Detail(await deactivated.Content.ReadAsStringAsync())
                .Should().Be(Detail(await unknown.Content.ReadAsStringAsync()));
            (deactivated.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : [])
                .Should().NotContain(c => c.StartsWith(".Nocturne.RecoverySession="), "no recovery session may start");

            using var scope = Fixture.Services.CreateScope();
            (await scope.ServiceProvider.GetRequiredService<IRecoveryCodeService>().GetRemainingCountAsync(memberId))
                .Should().Be(codes.Count, "a refused attempt does not spend the code");
        }
        finally
        {
            await conn.ExecuteAsync("DELETE FROM tenant_members WHERE subject_id = @id;", ("id", memberId));
        }
    }

    private static string? Detail(string problemJson) =>
        JsonDocument.Parse(problemJson).RootElement.GetProperty("detail").GetString();
}

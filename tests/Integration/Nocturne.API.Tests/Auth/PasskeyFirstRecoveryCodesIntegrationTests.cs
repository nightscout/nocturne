using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Fido2NetLib;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// A passkey sign-in through the real API gives a member who has never held recovery codes their
/// first set, once, when the caller shows them (<see cref="IRecoveryCodeService.GenerateFirstCodesAsync"/>).
/// A member whose codes were used up or reset, or who holds no passkey, gets none.
/// </summary>
[Collection(ApiIntegrationShards.Three)]
[Trait("Category", "Integration")]
public class PasskeyFirstRecoveryCodesIntegrationTests : ApiIntegrationTestBase
{
    private readonly List<Guid> _members = [];

    public PasskeyFirstRecoveryCodesIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    public override async Task DisposeAsync()
    {
        await using (var conn = await OpenPostgresAsync())
        await conn.ExecuteAsync(
            """
            DELETE FROM recovery_codes WHERE subject_id = ANY(@ids);
            DELETE FROM tenant_members WHERE subject_id = ANY(@ids);
            """,
            ("ids", _members.ToArray()));

        await base.DisposeAsync();
    }

    /// <summary>
    /// The state a setup leaves when it stops after the passkey is stored: an owner with a passkey
    /// and no recovery codes.
    /// </summary>
    [Fact]
    public async Task A_member_who_has_never_held_codes_gets_them_at_the_next_passkey_sign_in_and_only_then()
    {
        var (memberId, key) = await SeedPasskeyMemberAsync("Stopped Setup Owner");
        using var client = Fixture.CreateHttpClient(ServiceNames.NocturneApi);

        var first = await RecoveryCodesFromSignInAsync(client, memberId, key);

        first.Should().HaveCount(8);
        (await CodeRowsAsync(memberId)).Should().Be(8);
        (await WithCodesAsync(codes => codes.VerifyAndConsumeAsync(memberId, first![0]))).Should().BeTrue();

        (await RecoveryCodesFromSignInAsync(client, memberId, key)).Should().BeNull();
        (await WithCodesAsync(codes => codes.GetRemainingCountAsync(memberId))).Should().Be(7);
    }

    /// <summary>
    /// A caller that does not show codes, such as a page built before it could, would drop them, and
    /// the stored rows would then keep the member from ever being shown a first set.
    /// </summary>
    [Fact]
    public async Task A_sign_in_that_does_not_show_codes_makes_none_and_a_later_one_that_does_gets_them()
    {
        var (memberId, key) = await SeedPasskeyMemberAsync("Stale Page Member");
        using var client = Fixture.CreateHttpClient(ServiceNames.NocturneApi);

        (await RecoveryCodesFromSignInAsync(client, memberId, key, showsRecoveryCodes: false)).Should().BeNull();
        (await CodeRowsAsync(memberId)).Should().Be(0);

        (await RecoveryCodesFromSignInAsync(client, memberId, key)).Should().HaveCount(8);
        (await CodeRowsAsync(memberId)).Should().Be(8);
    }

    [Fact]
    public async Task A_member_whose_codes_are_used_up_gets_none()
    {
        var (memberId, key) = await SeedPasskeyMemberAsync("Used Up Member");
        await WithCodesAsync(async codes =>
        {
            foreach (var code in await codes.GenerateCodesAsync(memberId))
                await codes.VerifyAndConsumeAsync(memberId, code);
            return true;
        });
        using var client = Fixture.CreateHttpClient(ServiceNames.NocturneApi);

        (await RecoveryCodesFromSignInAsync(client, memberId, key)).Should().BeNull();

        (await WithCodesAsync(codes => codes.GetRemainingCountAsync(memberId))).Should().Be(0);
    }

    [Fact]
    public async Task A_member_whose_codes_were_reset_gets_none_and_stays_reset()
    {
        var (memberId, key) = await SeedPasskeyMemberAsync("Reset Member");
        await WithCodesAsync(async codes =>
        {
            await codes.GenerateCodesAsync(memberId);
            await codes.InvalidateAllAsync(memberId);
            return true;
        });
        using var client = Fixture.CreateHttpClient(ServiceNames.NocturneApi);

        (await RecoveryCodesFromSignInAsync(client, memberId, key)).Should().BeNull();

        (await WithCodesAsync(codes => codes.WereCodesResetAsync(memberId))).Should().BeTrue();
    }

    /// <summary>
    /// Provider-only members get no first codes by structure, not by a check in the rule: the rule
    /// runs only after a passkey assertion, which such a member cannot complete.
    /// </summary>
    [Fact]
    public async Task A_member_with_no_passkey_cannot_complete_the_passkey_sign_in_that_issues_first_codes()
    {
        await using var conn = await OpenPostgresAsync();
        var tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        var (memberId, _) = await AuthTestHelpers.SeedSubjectWithoutPasskeyAsync(conn, tenantId, "Provider Only Member");
        _members.Add(memberId);
        using var key = RSA.Create(2048);
        using var client = Fixture.CreateHttpClient(ServiceNames.NocturneApi);

        using var response = await PasskeyAssertions.SignInAsync(
            client, memberId, key, RpId(), showsRecoveryCodes: true);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await CodeRowsAsync(memberId)).Should().Be(0);
    }

    private async Task<(Guid MemberId, RSA Key)> SeedPasskeyMemberAsync(string name)
    {
        await using var conn = await OpenPostgresAsync();
        var tenantId = await AuthTestHelpers.GetTenantIdAsync(conn);
        var (memberId, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, tenantId, name);
        _members.Add(memberId);
        return (memberId, await PasskeyAssertions.StoreKeyAsync(conn, memberId));
    }

    /// <summary>The codes a successful passkey sign-in answered with, or null when it answered with none.</summary>
    private async Task<List<string>?> RecoveryCodesFromSignInAsync(
        HttpClient client, Guid memberId, RSA key, bool showsRecoveryCodes = true)
    {
        using var response = await PasskeyAssertions.SignInAsync(client, memberId, key, RpId(), showsRecoveryCodes);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var root = JsonDocument.Parse(body).RootElement;
        root.GetProperty("accessToken").GetString().Should().NotBeNullOrEmpty();
        return root.TryGetProperty("recoveryCodes", out var codes) && codes.ValueKind == JsonValueKind.Array
            ? codes.EnumerateArray().Select(c => c.GetString()!).ToList()
            : null;
    }

    private async Task<T> WithCodesAsync<T>(Func<IRecoveryCodeService, Task<T>> use)
    {
        using var scope = Fixture.Services.CreateScope();
        return await use(scope.ServiceProvider.GetRequiredService<IRecoveryCodeService>());
    }

    private async Task<long> CodeRowsAsync(Guid subjectId)
    {
        await using var conn = await OpenPostgresAsync();
        return await conn.ScalarAsync<long>("SELECT count(*) FROM recovery_codes WHERE subject_id = @subject", ("subject", subjectId));
    }

    private string RpId() => Fixture.Services.GetRequiredService<IOptions<Fido2Configuration>>().Value.RPID;
}

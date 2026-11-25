using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Services.Auth;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// Wrong authenticator codes at the sign-in step are bounded per step-up token and per member,
/// whatever client address each one comes from.
/// </summary>
[Collection(ApiIntegrationShards.Four)]
[Trait("Category", "Integration")]
public class StepUpWrongCodeBoundIntegrationTests : ApiIntegrationTestBase
{
    public StepUpWrongCodeBoundIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    [Fact]
    public async Task A_step_up_token_is_spent_after_its_wrong_codes_however_many_addresses_send_them()
    {
        var (subjectId, secrets) = await SeedMemberWithAuthenticatorsAsync("Token Bound Member", 1);
        var stepUp = await MintStepUpTokenAsync(subjectId);

        foreach (var code in WrongCodes(secrets).Take(StepUpCodeAttempts.WrongCodesPerToken + 7))
            (await LoginFromNewAddressAsync(stepUp, code)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await LoginFromNewAddressAsync(stepUp, CurrentCode(secrets[0])))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "the token's wrong codes are spent, so the first factor is needed again");
    }

    [Fact]
    public async Task A_fresh_step_up_token_does_not_reset_the_members_wrong_codes()
    {
        var (subjectId, secrets) = await SeedMemberWithAuthenticatorsAsync("Member Bound Member", 1);
        var wrong = WrongCodes(secrets).GetEnumerator();
        var perToken = StepUpCodeAttempts.WrongCodesPerToken - 1;

        var refused = new List<HttpStatusCode>();
        for (var sent = 0; sent < StepUpCodeAttempts.WrongCodesPerMember;)
        {
            var stepUp = await MintStepUpTokenAsync(subjectId);
            for (var i = 0; i < perToken && sent < StepUpCodeAttempts.WrongCodesPerMember; i++, sent++)
            {
                wrong.MoveNext();
                refused.Add((await LoginFromNewAddressAsync(stepUp, wrong.Current)).StatusCode);
            }
        }

        refused.Should().AllBeEquivalentTo(HttpStatusCode.BadRequest);

        using var locked = await LoginFromNewAddressAsync(await MintStepUpTokenAsync(subjectId), CurrentCode(secrets[0]));
        locked.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        locked.Headers.RetryAfter.Should().NotBeNull();

        await using var conn = await OpenPostgresAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT count(*) FROM auth_audit_log
            WHERE event_type = @type AND target_subject_id = @subject AND success = false
              AND details->>'method' = 'totp' AND details->>'reason' = 'too_many_wrong_codes';
            """;
        cmd.Parameters.AddWithValue("type", AuthAuditEventType.FailedAuth);
        cmd.Parameters.AddWithValue("subject", subjectId);
        ((long)(await cmd.ExecuteScalarAsync())!).Should().Be(1);
    }

    [Fact]
    public async Task A_correct_code_within_the_allowance_signs_in_and_clears_the_members_wrong_codes()
    {
        var (subjectId, secrets) = await SeedMemberWithAuthenticatorsAsync("Mistyping Member", 2);
        var wrong = WrongCodes(secrets).GetEnumerator();
        var perToken = StepUpCodeAttempts.WrongCodesPerToken - 1;

        for (var credential = 0; credential < 2; credential++)
        {
            for (var token = 0; token < 2; token++)
            {
                var stepUp = await MintStepUpTokenAsync(subjectId);
                for (var i = 0; i < perToken; i++)
                {
                    wrong.MoveNext();
                    (await LoginFromNewAddressAsync(stepUp, wrong.Current)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
                }

                if (token == 1)
                {
                    (await LoginFromNewAddressAsync(stepUp, CurrentCode(secrets[credential])))
                        .StatusCode.Should().Be(HttpStatusCode.OK);
                }
            }
        }
    }

    private async Task<HttpResponseMessage> LoginFromNewAddressAsync(string stepUpToken, string code)
    {
        using var client = CreateHttpClient(ServiceNames.NocturneApi);
        return await client.PostAsJsonAsync("/api/auth/totp/login", new { stepUpToken, code });
    }

    private Task<string> MintStepUpTokenAsync(Guid subjectId) =>
        WithTenantScopeAsync(services => services.GetRequiredService<ITotpService>()
            .CreateStepUpTokenAsync(subjectId, Fixture.TenantId, admittedByInvite: false));

    private async Task<(Guid SubjectId, byte[][] Secrets)> SeedMemberWithAuthenticatorsAsync(string name, int count)
    {
        await using var conn = await OpenPostgresAsync();
        var (subjectId, _) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(conn, Fixture.TenantId, name);

        var secrets = Enumerable.Range(0, count).Select(_ => TotpHelper.GenerateSecret()).ToArray();
        foreach (var secret in secrets)
        {
            await WithTenantScopeAsync(services => services.GetRequiredService<ITotpCredentialRepository>()
                .CreateAsync(new NewTotpCredential(subjectId, secret, null, DateTime.UtcNow, null, null), default));
        }

        return (subjectId, secrets);
    }

    private static string CurrentCode(byte[] secret) =>
        TotpHelper.ComputeTotp(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    /// <summary>
    /// Six-digit codes none of <paramref name="secrets"/> accepts in any step near now, so each one is
    /// wrong for the whole test.
    /// </summary>
    private static IEnumerable<string> WrongCodes(byte[][] secrets)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var valid = secrets
            .SelectMany(s => Enumerable.Range(-20, 41).Select(i => TotpHelper.ComputeTotp(s, now + i * 30L)))
            .ToHashSet();
        return Enumerable.Range(100_000, 900_000).Select(n => n.ToString()).Where(c => !valid.Contains(c));
    }
}

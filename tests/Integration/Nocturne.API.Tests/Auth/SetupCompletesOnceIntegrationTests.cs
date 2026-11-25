using System.Net.Http.Json;
using System.Security.Cryptography;
using FluentAssertions;
using Fido2NetLib;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Controllers.V4;
using Nocturne.API.Services.Auth;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;
using Nocturne.Tests.Shared.Infrastructure;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// First-run setup is completed by one caller: the first credential store claims the setup, so a
/// second completion that was already under way stores nothing, is answered as after setup closed,
/// and is not signed in.
/// </summary>
/// <remarks>
/// Both owner steps run over HTTP (<c>POST /api/v4/setup/owner/options</c>), so the two callers hold
/// different challenges for the one owner subject. Each completion runs
/// <see cref="SetupController"/> in a scope of the running API, with the real passkey service,
/// repositories and sign-in, and the first caller is held between its setup-open check and its
/// credential store by a test double: the passkey store for a passkey, and the provider service for
/// a provider, whose code exchange is not driven (it needs a live provider) and which links the
/// identity through <see cref="OidcAuthService.CompleteSetupAsync"/>. Routing, the rate limiter and
/// the middleware are not exercised.
/// </remarks>
[Collection(ApiIntegrationShards.Two)]
[Trait("Category", "Integration")]
public class SetupCompletesOnceIntegrationTests : ApiIntegrationTestBase
{
    private readonly string _issuer = $"https://setup-once-{Guid.NewGuid():N}.example";
    private readonly List<Guid> _deactivated = [];
    private Guid _providerId;
    private Guid _owner;
    private object _recordBefore = DBNull.Value;

    public SetupCompletesOnceIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        await using var conn = await OpenPostgresAsync();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE subjects SET is_active = false
                WHERE is_active AND NOT is_system_subject AND NOT is_demo_subject
                RETURNING id;
                """;
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                _deactivated.Add(reader.GetGuid(0));
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT setup_owner_subject_id FROM tenants WHERE id = @tenant";
            cmd.Parameters.AddWithValue("tenant", Fixture.TenantId);
            _recordBefore = await cmd.ExecuteScalarAsync() ?? DBNull.Value;
        }

        _providerId = Guid.CreateVersion7();
        await conn.ExecuteAsync("""
            DELETE FROM passkey_credentials WHERE subject_id IN
                (SELECT subject_id FROM tenant_members WHERE tenant_id = @tenant);
            INSERT INTO oidc_providers (id, name, issuer_url, client_id, is_enabled, display_order, scopes, claim_mappings, default_roles, created_at, updated_at)
            VALUES (@provider, 'Setup once IdP', @issuer, @client, true, 0, '["openid"]'::jsonb, '{}'::jsonb, '[]'::jsonb, now(), now());
            """,
            ("tenant", Fixture.TenantId), ("provider", _providerId), ("issuer", _issuer),
            ("client", $"client-{_providerId:N}"));
    }

    public override async Task DisposeAsync()
    {
        await using (var conn = await OpenPostgresAsync())
        {
            await conn.ExecuteAsync("""
                DELETE FROM passkey_credentials WHERE subject_id = @owner;
                DELETE FROM refresh_tokens WHERE subject_id = @owner;
                DELETE FROM recovery_codes WHERE subject_id = @owner;
                DELETE FROM subject_oidc_identities WHERE provider_id = @provider;
                UPDATE subjects SET is_platform_admin = false WHERE id = @owner;
                UPDATE subjects SET is_active = false
                WHERE is_active AND NOT is_system_subject AND NOT is_demo_subject;
                UPDATE subjects SET is_active = true WHERE id = ANY(@restore);
                DELETE FROM oidc_providers WHERE id = @provider;
                UPDATE tenants SET setup_owner_subject_id = @record WHERE id = @tenant;
                """,
                ("owner", _owner), ("provider", _providerId), ("restore", _deactivated.ToArray()),
                ("record", _recordBefore), ("tenant", Fixture.TenantId));
        }

        await base.DisposeAsync();
    }

    public enum Way
    {
        Passkey,
        Provider,
    }

    /// <summary>What a completion that lost the claim is answered: what the setup-open check answers once setup has closed.</summary>
    private static string LoserAnswer(Way way) => way == Way.Passkey
        ? """409 {"error":"owner_already_exists"}"""
        : "redirect /setup?error=setup_already_complete";

    [Theory]
    [InlineData(Way.Passkey, Way.Passkey)]
    [InlineData(Way.Provider, Way.Provider)]
    [InlineData(Way.Passkey, Way.Provider)]
    [InlineData(Way.Provider, Way.Passkey)]
    public async Task Two_completions_under_way_at_once_store_one_credential_and_sign_one_caller_in(
        Way held, Way other)
    {
        var heldStep = await OwnerStepAsync("The Operator");
        var otherStep = await OwnerStepAsync("Someone Else");
        _owner = (await OwnerSubjectIdsAsync()).Should().ContainSingle().Subject;

        var hold = new Hold();
        var first = Task.Run(() => CompleteAsync(held, heldStep, "held-sub", hold));
        (await Task.WhenAny(first, hold.Reached.Task.WaitAsync(Eventually.DefaultPatience)))
            .Should().NotBeSameAs(first, "the held caller stops between its setup-open check and its store");

        var second = await CompleteAsync(other, otherStep, "other-sub", hold: null);
        hold.Release();
        var firstAnswer = await first.WaitAsync(Eventually.DefaultPatience);

        Output.WriteLine($"held ({held}): {firstAnswer}");
        Output.WriteLine($"other ({other}): {second}");
        second.SignedIn.Should().BeTrue("the completion that stored first is signed in");
        firstAnswer.Should().Be(new Answer(LoserAnswer(held), SignedIn: false, RecoveryCodes: null));
        (await CredentialCountAsync(_owner)).Should().Be(1, "exactly one caller's credential is stored on the owner");
        (await LinkedIdentityCountAsync()).Should().Be(other == Way.Provider ? 1 : 0, "the loser links no identity");
        (await LiveSessionCountAsync(_owner)).Should().Be(1);
        (await RecoveryCodeCountAsync(_owner)).Should().Be(second.RecoveryCodes ?? 0, "only the winner's codes are stored");
        (await LoginRowCountAsync(_owner)).Should().Be(1);
        (await IsPlatformAdminAsync(_owner)).Should().BeTrue();
        (await RecordAsync()).Should().BeNull();
    }

    [Theory]
    [InlineData(Way.Passkey, "passkey_credentials")]
    [InlineData(Way.Provider, "subject_oidc_identities")]
    public async Task A_store_that_fails_after_the_claim_leaves_setup_open_for_the_next_completion(Way way, string table)
    {
        var step = await OwnerStepAsync("The Operator");
        _owner = (await OwnerSubjectIdsAsync()).Should().ContainSingle().Subject;

        var trigger = $"refuse_store_{_owner:N}";
        await using (var conn = await OpenPostgresAsync())
        {
            await conn.ExecuteAsync($"""
                CREATE FUNCTION {trigger}() RETURNS trigger LANGUAGE plpgsql AS
                    $$ BEGIN RAISE EXCEPTION 'store refused'; END $$;
                CREATE TRIGGER {trigger} BEFORE INSERT ON {table} FOR EACH ROW
                    WHEN (NEW.subject_id = '{_owner}') EXECUTE FUNCTION {trigger}();
                """);
        }

        try
        {
            var complete = () => CompleteAsync(way, step, "abandoned-sub", hold: null);

            await complete.Should().ThrowAsync<DbUpdateException>();
        }
        finally
        {
            await using var conn = await OpenPostgresAsync();
            await conn.ExecuteAsync($"""
                DROP TRIGGER {trigger} ON {table};
                DROP FUNCTION {trigger}();
                """);
        }

        (await RecordAsync()).Should().Be(_owner, "the failed store's claim rolled back with it");
        (await CredentialCountAsync(_owner)).Should().Be(0);
        (await LiveSessionCountAsync(_owner)).Should().Be(0);

        var retry = await OwnerStepAsync("The Operator");
        (await OwnerSubjectIdsAsync()).Should().Equal(_owner);
        var answer = await CompleteAsync(way, retry, "abandoned-sub", hold: null);

        answer.SignedIn.Should().BeTrue(answer.ToString());
        (await CredentialCountAsync(_owner)).Should().Be(1);
        (await RecordAsync()).Should().BeNull();
    }

    /// <remarks>
    /// The passkey completion reads the record for its subject before the ceremony and is refused
    /// there; the claim refuses it at the store when the record changes after that read.
    /// </remarks>
    [Theory]
    [InlineData(Way.Passkey, "400 Start setup again to create the owner account")]
    [InlineData(Way.Provider, "redirect /setup?error=setup_already_complete")]
    public async Task A_completion_for_a_subject_the_setup_owner_record_does_not_name_stores_nothing(Way way, string refused)
    {
        var step = await OwnerStepAsync("The Operator");
        _owner = (await OwnerSubjectIdsAsync()).Should().ContainSingle().Subject;
        await using (var conn = await OpenPostgresAsync())
        {
            await conn.ExecuteAsync("UPDATE tenants SET setup_owner_subject_id = @other WHERE id = @tenant",
                ("other", Fixture.OwnerSubjectId), ("tenant", Fixture.TenantId));
        }

        var answer = await CompleteAsync(way, step, "unnamed-sub", hold: null);

        answer.Should().Be(new Answer(refused, SignedIn: false, RecoveryCodes: null));
        (await CredentialCountAsync(_owner)).Should().Be(0);
        (await LinkedIdentityCountAsync()).Should().Be(0);
        (await LiveSessionCountAsync(_owner)).Should().Be(0);
        (await LoginRowCountAsync(_owner)).Should().Be(0);
        (await RecordAsync()).Should().Be(Fixture.OwnerSubjectId);
    }

    /// <summary>
    /// A caller's answer: what the action returned, whether it set a session cookie, and how many
    /// recovery codes it was given.
    /// </summary>
    private sealed record Answer(string Result, bool SignedIn, int? RecoveryCodes)
    {
        public override string ToString() => $"{Result}, signed in: {SignedIn}";
    }

    /// <summary>Holds the first caller at its store until the test releases it.</summary>
    private sealed class Hold
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task PassAsync()
        {
            Reached.TrySetResult();
            await _released.Task.WaitAsync(Eventually.DefaultPatience);
        }

        public void Release() => _released.TrySetResult();
    }

    /// <summary>The passkey store, which waits on <paramref name="hold"/> before the setup owner's store.</summary>
    private sealed class HeldPasskeyStore(IPasskeyCredentialRepository inner, Hold? hold) : IPasskeyCredentialRepository
    {
        public Task<Guid> CreateAsync(NewPasskeyCredential credential, CancellationToken ct) =>
            inner.CreateAsync(credential, ct);

        public async Task<Guid?> CreateForSetupOwnerAsync(Guid tenantId, NewPasskeyCredential credential, CancellationToken ct)
        {
            if (hold is not null)
                await hold.PassAsync();
            return await inner.CreateForSetupOwnerAsync(tenantId, credential, ct);
        }

        public Task<StoredPasskey?> FindByCredentialIdAsync(byte[] credentialId, CancellationToken ct) =>
            inner.FindByCredentialIdAsync(credentialId, ct);

        public Task<bool> ExistsAsync(byte[] credentialId, CancellationToken ct) => inner.ExistsAsync(credentialId, ct);

        public Task<IReadOnlyList<byte[]>> GetCredentialIdsAsync(Guid subjectId, CancellationToken ct) =>
            inner.GetCredentialIdsAsync(subjectId, ct);

        public Task<int> CountAsync(Guid subjectId, CancellationToken ct) => inner.CountAsync(subjectId, ct);

        public Task<IReadOnlyList<PasskeyCredentialRecord>> ListAsync(Guid subjectId, CancellationToken ct) =>
            inner.ListAsync(subjectId, ct);

        public Task<IReadOnlyList<PasskeyHolder>> ListHoldersAsync(CancellationToken ct) => inner.ListHoldersAsync(ct);

        public Task<bool> RecordUseAsync(Guid credentialId, uint signCount, DateTime now, CancellationToken ct) =>
            inner.RecordUseAsync(credentialId, signCount, now, ct);

        public Task<bool> RemoveAsync(Guid credentialId, Guid subjectId, CancellationToken ct) =>
            inner.RemoveAsync(credentialId, subjectId, ct);
    }

    private async Task<SetupOwnerOptionsResponse> OwnerStepAsync(string displayName)
    {
        using var apex = Fixture.CreateApexClient();
        using var response = await apex.PostAsJsonAsync(
            "/api/v4/setup/owner/options", new { username = "operator", displayName });
        var body = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue(body);
        return (await response.Content.ReadFromJsonAsync<SetupOwnerOptionsResponse>())!;
    }

    private async Task<Answer> CompleteAsync(Way way, SetupOwnerOptionsResponse step, string externalId, Hold? hold)
    {
        using var scope = Fixture.Services.CreateScope();
        var services = scope.ServiceProvider;
        var oidcOptions = services.GetRequiredService<IOptions<OidcOptions>>().Value;

        var store = new HeldPasskeyStore(
            services.GetRequiredService<IPasskeyCredentialRepository>(), way == Way.Passkey ? hold : null);
        var passkeys = ActivatorUtilities.CreateInstance<PasskeyService>(services, store);

        var realOidc = (OidcAuthService)services.GetRequiredService<IOidcAuthService>();
        var provider = await services.GetRequiredService<IOidcProviderService>().GetProviderByIdAsync(_providerId);
        var oidc = new Mock<IOidcAuthService>(MockBehavior.Strict);
        oidc.Setup(o => o.HandleSetupCallbackAsync(externalId, "state", "state", Fixture.TenantId))
            .Returns(async () =>
            {
                if (hold is not null)
                    await hold.PassAsync();
                return await realOidc.CompleteSetupAsync(
                    new OidcAuthService.OidcStateData
                    {
                        Intent = "setup",
                        ProviderId = _providerId,
                        ReturnUrl = "/setup",
                        Nonce = "nonce",
                        SubjectId = _owner,
                        TenantSlug = ApiIntegrationTestFixture.TenantSlug,
                        CreatedAt = DateTimeOffset.UtcNow,
                        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                    },
                    provider!,
                    new OidcAuthService.OidcIdTokenClaims { Sub = externalId, Name = externalId },
                    Fixture.TenantId);
            });

        var controller = ActivatorUtilities.CreateInstance<SetupController>(
            services, (IPasskeyService)passkeys, oidc.Object);
        var http = new DefaultHttpContext { RequestServices = services };
        http.Request.Host = new HostString(new Uri(Fixture.ApiBaseUrl).Authority);
        http.Request.Headers.Cookie = $"{oidcOptions.Cookie.StateCookieName}=state";
        controller.ControllerContext = new ControllerContext { HttpContext = http };

        IActionResult result;
        if (way == Way.Passkey)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            result = await controller.OwnerComplete(
                new SetupOwnerCompleteRequest
                {
                    AttestationResponseJson = Attestation(key, step.Options),
                    ChallengeToken = step.ChallengeToken,
                },
                CancellationToken.None);
        }
        else
        {
            result = await controller.OidcCallback(externalId, "state", error: null, error_description: null, CancellationToken.None);
        }

        var signedIn = http.Response.Headers.SetCookie.Any(c => c?.StartsWith(oidcOptions.Cookie.AccessTokenName + "=") == true);
        var codes = result is ObjectResult { Value: SetupOwnerCompleteResponse completed } ? completed.RecoveryCodes.Count : (int?)null;
        return new Answer(Describe(result), signedIn, codes);
    }

    private static string Describe(IActionResult result) => result switch
    {
        RedirectResult redirect => $"redirect {redirect.Url}",
        ObjectResult { Value: SetupOwnerCompleteResponse completed } objectResult =>
            $"{objectResult.StatusCode} with {completed.RecoveryCodes.Count} recovery codes",
        ObjectResult { Value: ProblemDetails problem } objectResult => $"{objectResult.StatusCode} {problem.Detail}",
        ObjectResult objectResult => $"{objectResult.StatusCode} {System.Text.Json.JsonSerializer.Serialize(objectResult.Value)}",
        _ => result.GetType().Name,
    };

    /// <summary>A "none" attestation of a new P-256 credential, made by a browser on the API's host.</summary>
    private string Attestation(ECDsa key, string optionsJson)
    {
        var credentialId = Guid.CreateVersion7().ToByteArray();
        var authenticatorData = new Fido2NetLib.Objects.AuthenticatorData(
            SHA256.HashData("localhost"u8),
            Fido2NetLib.Objects.AuthenticatorFlags.UP | Fido2NetLib.Objects.AuthenticatorFlags.UV
                | Fido2NetLib.Objects.AuthenticatorFlags.AT,
            signCount: 0,
            new Fido2NetLib.Objects.AttestedCredentialData(
                Guid.Empty, credentialId,
                new Fido2NetLib.Objects.CredentialPublicKey(key, Fido2NetLib.Objects.COSE.Algorithm.ES256)),
            null).ToByteArray();

        using var options = System.Text.Json.JsonDocument.Parse(optionsJson);
        var clientData = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "webauthn.create",
            challenge = options.RootElement.GetProperty("challenge").GetString(),
            origin = $"https://{new Uri(Fixture.ApiBaseUrl).Authority}",
        });

        return System.Text.Json.JsonSerializer.Serialize(new AuthenticatorAttestationRawResponse
        {
            Id = System.Buffers.Text.Base64Url.EncodeToString(credentialId),
            RawId = credentialId,
            Type = Fido2NetLib.Objects.PublicKeyCredentialType.PublicKey,
            Response = new AuthenticatorAttestationRawResponse.AttestationResponse
            {
                AttestationObject = new Fido2NetLib.Cbor.CborMap
                {
                    { "fmt", "none" },
                    { "attStmt", new Fido2NetLib.Cbor.CborMap() },
                    { "authData", authenticatorData },
                }.Encode(),
                ClientDataJson = clientData,
            },
        });
    }

    private async Task<long> CredentialCountAsync(Guid subjectId)
    {
        await using var conn = await OpenPostgresAsync();
        return await conn.ScalarAsync<long>("""
            SELECT (SELECT count(*) FROM passkey_credentials WHERE subject_id = @subject)
                 + (SELECT count(*) FROM subject_oidc_identities WHERE subject_id = @subject);
            """, ("subject", subjectId));
    }

    private async Task<long> LinkedIdentityCountAsync()
    {
        await using var conn = await OpenPostgresAsync();
        return await conn.ScalarAsync<long>("SELECT count(*) FROM subject_oidc_identities WHERE provider_id = @provider", ("provider", _providerId));
    }

    private async Task<long> RecoveryCodeCountAsync(Guid subjectId)
    {
        await using var conn = await OpenPostgresAsync();
        return await conn.ScalarAsync<long>("SELECT count(*) FROM recovery_codes WHERE subject_id = @subject", ("subject", subjectId));
    }

    private async Task<long> LoginRowCountAsync(Guid subjectId)
    {
        await using var conn = await OpenPostgresAsync();
        return await conn.ScalarAsync<long>("SELECT count(*) FROM auth_audit_log WHERE target_subject_id = @subject AND event_type = @login", ("subject", subjectId), ("login", AuthAuditEventType.Login));
    }

    private async Task<Guid?> RecordAsync()
    {
        await using var conn = await OpenPostgresAsync();
        return await conn.ScalarAsync<Guid?>("SELECT setup_owner_subject_id FROM tenants WHERE id = @tenant", ("tenant", Fixture.TenantId));
    }

    private async Task<long> LiveSessionCountAsync(Guid subjectId)
    {
        await using var conn = await OpenPostgresAsync();
        return await conn.ScalarAsync<long>("SELECT count(*) FROM refresh_tokens WHERE subject_id = @subject AND revoked_at IS NULL AND expires_at > now()", ("subject", subjectId));
    }

    private async Task<bool> IsPlatformAdminAsync(Guid subjectId)
    {
        await using var conn = await OpenPostgresAsync();
        return await conn.ScalarAsync<bool>("SELECT is_platform_admin FROM subjects WHERE id = @subject", ("subject", subjectId));
    }

    private async Task<List<Guid>> OwnerSubjectIdsAsync()
    {
        await using var conn = await OpenPostgresAsync();
        return await conn.QueryAsync("""
            SELECT m.subject_id
            FROM tenant_members m
            JOIN tenant_member_roles mr ON mr.tenant_member_id = m.id
            JOIN tenant_roles r ON r.id = mr.tenant_role_id
            JOIN subjects s ON s.id = m.subject_id
            WHERE m.tenant_id = @tenant AND r.slug = 'owner' AND s.is_active
            ORDER BY m.subject_id;
            """, reader => reader.GetGuid(0), ("tenant", Fixture.TenantId));
    }
}

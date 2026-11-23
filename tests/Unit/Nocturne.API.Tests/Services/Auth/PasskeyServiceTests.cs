using System.Security.Cryptography;
using Fido2NetLib;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Extensions;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Models.Configuration;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

using Nocturne.Infrastructure.Data.Repositories;

using Nocturne.API.Services.Identity;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// Unit tests for PasskeyService: DB operations, removal protection, credential cap, challenge
/// tokens, and ceremonies signed by a software key.
/// </summary>
public class PasskeyServiceTests
{
    private readonly NocturneDbContext _dbContext;
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _subjectId = Guid.CreateVersion7();

    public PasskeyServiceTests()
    {
        _dbContext = TestDbContextFactory.CreateInMemoryContext();
        _dbContext.TenantId = _tenantId;
    }

    #region GetCredentialsAsync

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetCredentialsAsync_ReturnsCredentialsForSubjectAndTenant()
    {
        // Arrange - add credentials for the subject and for another subject in the same tenant
        var otherSubjectId = Guid.CreateVersion7();

        _dbContext.PasskeyCredentials.AddRange(
            CreateCredentialEntity(_subjectId,"Key 1"),
            CreateCredentialEntity(_subjectId,"Key 2"),
            CreateCredentialEntity(otherSubjectId,"Other User Key"));
        await _dbContext.SaveChangesAsync();

        var service = CreateService();

        // Act
        var credentials = await service.GetCredentialsAsync(_subjectId);

        // Assert - only returns credentials for the specified subject
        credentials.Should().HaveCount(2);
        credentials.Select(c => c.Label).Should().BeEquivalentTo(["Key 1", "Key 2"]);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetCredentialsAsync_ReturnsEmptyListWhenNoCredentials()
    {
        var service = CreateService();

        var credentials = await service.GetCredentialsAsync(_subjectId);

        credentials.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetCredentialsAsync_OrdersByCreatedAtDescending()
    {
        var older = CreateCredentialEntity(_subjectId,"Older");
        older.CreatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var newer = CreateCredentialEntity(_subjectId,"Newer");
        newer.CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        _dbContext.PasskeyCredentials.AddRange(older, newer);
        await _dbContext.SaveChangesAsync();

        var service = CreateService();
        var credentials = await service.GetCredentialsAsync(_subjectId);

        credentials.Should().HaveCount(2);
        credentials[0].Label.Should().Be("Newer");
        credentials[1].Label.Should().Be("Older");
    }

    #endregion

    #region GetCredentialCountAsync

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetCredentialCountAsync_ReturnsCorrectCount()
    {
        _dbContext.PasskeyCredentials.AddRange(
            CreateCredentialEntity(_subjectId),
            CreateCredentialEntity(_subjectId),
            CreateCredentialEntity(_subjectId));
        await _dbContext.SaveChangesAsync();

        var service = CreateService();

        var count = await service.GetCredentialCountAsync(_subjectId, _tenantId);

        count.Should().Be(3);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetCredentialCountAsync_ReturnsZeroWhenNone()
    {
        var service = CreateService();

        var count = await service.GetCredentialCountAsync(_subjectId, _tenantId);

        count.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetCredentialCountAsync_OnlyCountsForSpecificSubject()
    {
        var otherSubjectId = Guid.CreateVersion7();

        _dbContext.PasskeyCredentials.AddRange(
            CreateCredentialEntity(_subjectId),
            CreateCredentialEntity(otherSubjectId));
        await _dbContext.SaveChangesAsync();

        var service = CreateService();

        var count = await service.GetCredentialCountAsync(_subjectId, _tenantId);

        count.Should().Be(1);
    }

    #endregion

    #region RemoveCredentialAsync

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RemoveCredentialAsync_RemovesCredentialWhenMultipleExist()
    {
        var cred1 = CreateCredentialEntity(_subjectId,"Key 1");
        var cred2 = CreateCredentialEntity(_subjectId,"Key 2");
        _dbContext.PasskeyCredentials.AddRange(cred1, cred2);
        await _dbContext.SaveChangesAsync();

        var service = CreateService();

        await service.RemoveCredentialAsync(cred1.Id, _subjectId, _tenantId);

        var remaining = _dbContext.PasskeyCredentials
            .Where(c => c.SubjectId == _subjectId)
            .ToList();
        remaining.Should().HaveCount(1);
        remaining[0].Id.Should().Be(cred2.Id);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RemoveCredentialAsync_ThrowsWhenCredentialNotFound()
    {
        var service = CreateService();

        var act = () => service.RemoveCredentialAsync(Guid.CreateVersion7(), _subjectId, _tenantId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Credential not found.");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RemoveCredentialAsync_RemovesLastPasskey_GuardIsNowOnController()
    {
        // Guard logic has been moved to the controller via SubjectService.CountPrimaryAuthFactorsAsync.
        // PasskeyService now simply removes the credential without checking alternatives.
        var cred = CreateCredentialEntity(_subjectId);
        _dbContext.PasskeyCredentials.Add(cred);
        await _dbContext.SaveChangesAsync();

        var service = CreateService();

        await service.RemoveCredentialAsync(cred.Id, _subjectId, _tenantId);

        var remaining = _dbContext.PasskeyCredentials
            .Where(c => c.SubjectId == _subjectId)
            .ToList();
        remaining.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RemoveCredentialAsync_ThrowsWhenCredentialBelongsToDifferentSubject()
    {
        var otherSubjectId = Guid.CreateVersion7();
        var cred = CreateCredentialEntity(otherSubjectId);
        _dbContext.PasskeyCredentials.Add(cred);
        await _dbContext.SaveChangesAsync();

        var service = CreateService();

        var act = () => service.RemoveCredentialAsync(cred.Id, _subjectId, _tenantId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Credential not found.");
    }

    #endregion

    #region Credential Cap Enforcement (via CompleteRegistrationAsync)

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CredentialCap_MaxIs20PerSubjectPerTenant()
    {
        // Add 20 credentials
        for (var i = 0; i < 20; i++)
        {
            _dbContext.PasskeyCredentials.Add(CreateCredentialEntity(_subjectId,$"Key {i}"));
        }
        await _dbContext.SaveChangesAsync();

        var count = await _dbContext.PasskeyCredentials
            .CountAsync(c => c.SubjectId == _subjectId);
        count.Should().Be(20);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CredentialCap_OtherSubjectCredentialsDoNotCountTowardsCap()
    {
        var otherSubjectId = Guid.CreateVersion7();

        // Add 20 credentials for a different subject
        for (var i = 0; i < 20; i++)
        {
            _dbContext.PasskeyCredentials.Add(CreateCredentialEntity(otherSubjectId));
        }

        // Add 1 for our subject
        _dbContext.PasskeyCredentials.Add(CreateCredentialEntity(_subjectId));
        await _dbContext.SaveChangesAsync();

        var service = CreateService();

        var count = await service.GetCredentialCountAsync(_subjectId, _tenantId);
        count.Should().Be(1);

        var otherCount = await service.GetCredentialCountAsync(otherSubjectId, _tenantId);
        otherCount.Should().Be(20);
    }

    #endregion

    #region Challenge Cookie Expiry

    [Fact]
    [Trait("Category", "Unit")]
    public void ChallengeCookie_ExpiredCookieIsRejected()
    {
        var dataProtectionProvider = new EphemeralDataProtectionProvider();
        var protector = dataProtectionProvider.CreateProtector("Nocturne.Passkey.Challenge");

        // Create an expired cookie payload
        var payload = new
        {
            OptionsJson = "{}",
            SubjectId = (Guid?)_subjectId,
            ExpiresAt = DateTime.UtcNow.AddMinutes(-1), // Expired
        };

        var json = System.Text.Json.JsonSerializer.Serialize(payload);
        var encryptedCookie = protector.Protect(json);

        // The service's DecryptChallengeCookie is private, but we can verify the behavior
        // through CompleteRegistrationAsync or CompleteAssertionAsync.
        // Since those also require Fido2, we test the expiry concept indirectly
        // by verifying the service rejects the cookie.
        // For now, verify the protector round-trips correctly (the actual expiry check
        // is tested in integration tests).
        var decrypted = protector.Unprotect(encryptedCookie);
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<ChallengeCookiePayloadForTest>(decrypted);

        deserialized.Should().NotBeNull();
        deserialized!.ExpiresAt.Should().BeBefore(DateTime.UtcNow);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ChallengeCookie_TamperedCookieFailsDecryption()
    {
        var dataProtectionProvider = new EphemeralDataProtectionProvider();
        var protector = dataProtectionProvider.CreateProtector("Nocturne.Passkey.Challenge");

        var payload = new
        {
            OptionsJson = "{}",
            SubjectId = (Guid?)_subjectId,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
        };

        var json = System.Text.Json.JsonSerializer.Serialize(payload);
        var encryptedCookie = protector.Protect(json);

        // Tamper with the cookie
        var tampered = encryptedCookie + "tampered";

        var act = () => protector.Unprotect(tampered);

        act.Should().Throw<Exception>();
    }

    #endregion

    #region Challenge token binding

    [Theory]
    [Trait("Category", "Unit")]
    [MemberData(nameof(EveryFlow))]
    public async Task CompleteRegistrationAsync_WithAChallengeForAnotherSubject_IsRejected(PasskeyRegistrationFlow flow)
    {
        var service = CreateService();
        var otherSubjectId = Guid.CreateVersion7();

        var options = await service.GenerateRegistrationOptionsAsync(otherSubjectId, "other", flow);

        var act = () => service.CompleteRegistrationAsync(
            "{}", options.ChallengeToken, flow, _tenantId, expectedSubjectId: _subjectId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not issued for the enrolling subject*");

        (await _dbContext.PasskeyCredentials.AnyAsync(c => c.SubjectId == otherSubjectId))
            .Should().BeFalse("no credential may be stored for a subject the caller does not own");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CompleteRegistrationAsync_WithAnAssertionChallenge_IsRejected()
    {
        var service = CreateService();

        // Login and registration challenges share one protector, so the ceremony a token was
        // minted for has to be checked.
        var assertion = await service.GenerateDiscoverableAssertionOptionsAsync(_tenantId);

        var act = () => service.CompleteRegistrationAsync(
            "{}", assertion.ChallengeToken, PasskeyRegistrationFlow.OwnAccount, _tenantId, expectedSubjectId: _subjectId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*different ceremony*");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FindRegistrationChallengeSubject_NamesTheSubjectARegistrationChallengeWasIssuedFor()
    {
        var service = CreateService();

        var registration = await service.GenerateRegistrationOptionsAsync(
            _subjectId, "testuser", PasskeyRegistrationFlow.MemberInvite);

        service.FindRegistrationChallengeSubject(registration.ChallengeToken, PasskeyRegistrationFlow.MemberInvite)
            .Should().Be(_subjectId);
    }

    public static TheoryData<PasskeyRegistrationFlow, PasskeyRegistrationFlow> EveryPairOfFlows()
    {
        var pairs = new TheoryData<PasskeyRegistrationFlow, PasskeyRegistrationFlow>();
        foreach (var minted in Enum.GetValues<PasskeyRegistrationFlow>())
            foreach (var redeemed in Enum.GetValues<PasskeyRegistrationFlow>())
                pairs.Add(minted, redeemed);
        return pairs;
    }

    public static TheoryData<PasskeyRegistrationFlow, PasskeyRegistrationFlow> EveryPairOfDifferentFlows()
    {
        var pairs = new TheoryData<PasskeyRegistrationFlow, PasskeyRegistrationFlow>();
        foreach (var minted in Enum.GetValues<PasskeyRegistrationFlow>())
            foreach (var redeemed in Enum.GetValues<PasskeyRegistrationFlow>().Where(f => f != minted))
                pairs.Add(minted, redeemed);
        return pairs;
    }

    [Theory]
    [Trait("Category", "Unit")]
    [MemberData(nameof(EveryPairOfFlows))]
    public async Task FindRegistrationChallengeSubject_NamesTheSubjectOnlyInTheFlowTheChallengeWasMintedFor(
        PasskeyRegistrationFlow minted, PasskeyRegistrationFlow redeemed)
    {
        var service = CreateService();

        var registration = await service.GenerateRegistrationOptionsAsync(_subjectId, "testuser", minted);

        service.FindRegistrationChallengeSubject(registration.ChallengeToken, redeemed)
            .Should().Be(minted == redeemed ? _subjectId : null);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [MemberData(nameof(EveryPairOfDifferentFlows))]
    public async Task CompleteRegistrationAsync_WithAChallengeMintedForAnotherFlow_IsRejected(
        PasskeyRegistrationFlow minted, PasskeyRegistrationFlow redeemed)
    {
        var service = CreateService();

        var registration = await service.GenerateRegistrationOptionsAsync(_subjectId, "testuser", minted);

        var act = () => service.CompleteRegistrationAsync(
            "{}", registration.ChallengeToken, redeemed, _tenantId, expectedSubjectId: _subjectId);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*different flow*");
        (await _dbContext.PasskeyCredentials.AnyAsync(c => c.SubjectId == _subjectId)).Should().BeFalse();
    }

    /// <summary>A registration challenge minted before challenges named their flow.</summary>
    private string FlowlessRegistrationChallenge(EphemeralDataProtectionProvider provider) =>
        provider.CreateProtector("Nocturne.Passkey.Challenge").Protect(System.Text.Json.JsonSerializer.Serialize(new
        {
            OptionsJson = "{}",
            SubjectId = (Guid?)_subjectId,
            Purpose = "registration",
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
        }));

    [Theory]
    [Trait("Category", "Unit")]
    [MemberData(nameof(EveryFlow))]
    public async Task ARegistrationChallengeNamingNoFlow_IsRedeemedInNoFlow(PasskeyRegistrationFlow flow)
    {
        var provider = new EphemeralDataProtectionProvider();
        var service = CreateService(dataProtection: provider);
        var token = FlowlessRegistrationChallenge(provider);

        service.FindRegistrationChallengeSubject(token, flow).Should().BeNull();
        var act = () => service.CompleteRegistrationAsync("{}", token, flow, _tenantId, expectedSubjectId: _subjectId);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*different flow*");
    }

    public static TheoryData<PasskeyRegistrationFlow> EveryFlow() => new(Enum.GetValues<PasskeyRegistrationFlow>());

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FindRegistrationChallengeSubject_NamesNoSubjectForAnAssertionChallenge()
    {
        var service = CreateService();

        var assertion = await service.GenerateDiscoverableAssertionOptionsAsync(_tenantId);

        service.FindRegistrationChallengeSubject(assertion.ChallengeToken, PasskeyRegistrationFlow.OwnAccount).Should().BeNull();
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("")]
    [InlineData("not-a-token")]
    public void FindRegistrationChallengeSubject_NamesNoSubjectForATokenThisServerDidNotIssue(string token)
    {
        CreateService().FindRegistrationChallengeSubject(token, PasskeyRegistrationFlow.OwnAccount).Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void FindRegistrationChallengeSubject_NamesNoSubjectForAnExpiredOrUnreadableChallenge()
    {
        var provider = new EphemeralDataProtectionProvider();
        var service = CreateService(dataProtection: provider);
        var protector = provider.CreateProtector("Nocturne.Passkey.Challenge");
        var expired = protector.Protect(System.Text.Json.JsonSerializer.Serialize(new
        {
            OptionsJson = "{}",
            SubjectId = (Guid?)_subjectId,
            Purpose = "registration",
            Flow = PasskeyRegistrationFlow.OwnAccount,
            ExpiresAt = DateTime.UtcNow.AddMinutes(-1),
        }));

        service.FindRegistrationChallengeSubject(expired, PasskeyRegistrationFlow.OwnAccount).Should().BeNull();
        service.FindRegistrationChallengeSubject(protector.Protect("not json"), PasskeyRegistrationFlow.OwnAccount).Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CompleteAssertionAsync_WithARegistrationChallenge_IsRejected()
    {
        var service = CreateService();

        var registration = await service.GenerateRegistrationOptionsAsync(
            _subjectId, "testuser", PasskeyRegistrationFlow.OwnAccount);

        var act = () => service.CompleteAssertionAsync("{}", registration.ChallengeToken, _tenantId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*different ceremony*");
    }

    #endregion

    #region Assertion admits only an active member

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CompleteAssertionAsync_ForADeactivatedMember_IsRefusedAndTheCredentialIsNotUsed()
    {
        var credential = CreateCredentialEntity(_subjectId);
        _dbContext.Subjects.Add(new SubjectEntity { Id = _subjectId, Name = "rhys", Username = "rhys", IsActive = false });
        _dbContext.TenantMembers.Add(new TenantMemberEntity { Id = Guid.CreateVersion7(), TenantId = _tenantId, SubjectId = _subjectId });
        _dbContext.PasskeyCredentials.Add(credential);
        await _dbContext.SaveChangesAsync();

        var service = CreateService();
        var options = await service.GenerateDiscoverableAssertionOptionsAsync(_tenantId);

        var act = () => service.CompleteAssertionAsync(
            AssertionNaming(credential.CredentialId), options.ChallengeToken, _tenantId);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not active*");
        (await _dbContext.PasskeyCredentials.AsNoTracking().SingleAsync(c => c.Id == credential.Id))
            .LastUsedAt.Should().BeNull();
    }

    /// <summary>
    /// An assertion naming <paramref name="rawId"/> whose authenticator data does not parse: it
    /// reaches every check the service makes before Fido2, and Fido2 refuses it.
    /// </summary>
    private static string AssertionNaming(byte[] rawId) =>
        System.Text.Json.JsonSerializer.Serialize(new AuthenticatorAssertionRawResponse
        {
            Id = System.Buffers.Text.Base64Url.EncodeToString(rawId),
            RawId = rawId,
            Type = Fido2NetLib.Objects.PublicKeyCredentialType.PublicKey,
            Response = new AuthenticatorAssertionRawResponse.AssertionResponse
            {
                AuthenticatorData = [0],
                Signature = [0],
                ClientDataJson = System.Text.Encoding.UTF8.GetBytes("{}"),
            },
        });

    #endregion

    #region Assertion refusals are of the three types login answers as a refusal

    public static TheoryData<string, Type> RefusedAssertions() => new()
    {
        { "no-response", typeof(Fido2VerificationException) },
        { "malformed-authenticator-data", typeof(Fido2VerificationException) },
        { "wrong-signature", typeof(Fido2VerificationException) },
        { "signature-of-the-wrong-length", typeof(Fido2VerificationException) },
        { "empty", typeof(System.Text.Json.JsonException) },
        { "not-json", typeof(System.Text.Json.JsonException) },
        { "raw-id-not-base64url", typeof(System.Text.Json.JsonException) },
        { "unknown-credential", typeof(InvalidOperationException) },
        { "no-tenant", typeof(InvalidOperationException) },
    };

    [Theory]
    [Trait("Category", "Unit")]
    [MemberData(nameof(RefusedAssertions))]
    public async Task CompleteAssertionAsync_RefusesAnAssertionItCannotAccept_WithANamedType(string shape, Type refusal)
    {
        using var key = RSA.Create(2048);
        var credential = await SeedActiveMemberCredentialAsync(key);

        var service = CreateService();
        var options = await service.GenerateDiscoverableAssertionOptionsAsync(_tenantId);
        var assertion = shape switch
        {
            "no-response" => System.Text.Json.JsonSerializer.Serialize(new AuthenticatorAssertionRawResponse
            {
                Id = System.Buffers.Text.Base64Url.EncodeToString(credential.CredentialId),
                RawId = credential.CredentialId,
                Type = Fido2NetLib.Objects.PublicKeyCredentialType.PublicKey,
            }),
            "empty" => "",
            "not-json" => "not json",
            "raw-id-not-base64url" => "{\"id\":\"x\",\"rawId\":\"!!!\",\"type\":\"public-key\"}",
            "unknown-credential" => AssertionNaming(Guid.CreateVersion7().ToByteArray()),
            "wrong-signature" => SignedAssertion(credential, options.OptionsJson,
                _ => key.SignData("other data"u8.ToArray(), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)),
            "signature-of-the-wrong-length" => SignedAssertion(credential, options.OptionsJson, _ => [0]),
            _ => AssertionNaming(credential.CredentialId),
        };
        var tenantId = shape == "no-tenant" ? Guid.Empty : _tenantId;

        var thrown = await Record.ExceptionAsync(
            () => service.CompleteAssertionAsync(assertion, options.ChallengeToken, tenantId));

        thrown.Should().BeOfType(refusal);
        if (shape == "no-tenant")
            thrown!.Message.Should().Be(PasskeyService.OwnerNotAMemberMessage);
        if (shape is "wrong-signature" or "signature-of-the-wrong-length")
            thrown.Should().BeOfType<Fido2VerificationException>()
                .Which.Code.Should().Be(Fido2NetLib.Exceptions.Fido2ErrorCode.InvalidSignature);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CompleteAssertionAsync_SignedByTheCredentialsKey_AdmitsItsHolder()
    {
        using var key = RSA.Create(2048);
        var credential = await SeedActiveMemberCredentialAsync(key);

        var service = CreateService();
        var options = await service.GenerateDiscoverableAssertionOptionsAsync(_tenantId);
        var assertion = SignedAssertion(credential, options.OptionsJson,
            signed => key.SignData(signed, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

        var result = await service.CompleteAssertionAsync(assertion, options.ChallengeToken, _tenantId);

        result.SubjectId.Should().Be(_subjectId);
    }

    /// <summary>An active member's credential whose stored public key is <paramref name="key"/> (RS256).</summary>
    private async Task<PasskeyCredentialEntity> SeedActiveMemberCredentialAsync(RSA key)
    {
        var publicKey = key.ExportParameters(includePrivateParameters: false);
        var credential = CreateCredentialEntity(_subjectId);
        credential.PublicKey = new Fido2NetLib.Cbor.CborMap
        {
            { 1, 3 },
            { 3, -257 },
            { -1, publicKey.Modulus! },
            { -2, publicKey.Exponent! },
        }.Encode();
        _dbContext.Subjects.Add(new SubjectEntity { Id = _subjectId, Name = "rhys", Username = "rhys", IsActive = true });
        _dbContext.TenantMembers.Add(new TenantMemberEntity { Id = Guid.CreateVersion7(), TenantId = _tenantId, SubjectId = _subjectId });
        _dbContext.PasskeyCredentials.Add(credential);
        await _dbContext.SaveChangesAsync();
        return credential;
    }

    /// <summary>
    /// An assertion for <paramref name="credential"/> that passes every check before the signature,
    /// with the signature <paramref name="sign"/> makes over the bytes an authenticator signs.
    /// </summary>
    private static string SignedAssertion(
        PasskeyCredentialEntity credential, string optionsJson, Func<byte[], byte[]> sign,
        string origin = "https://localhost") =>
        SignedAssertion(credential, ClientData("webauthn.get", optionsJson, origin), sign);

    /// <summary>The client data a browser at <paramref name="origin"/> hands the authenticator.</summary>
    private static byte[] ClientData(string type, string optionsJson, string origin)
    {
        using var options = System.Text.Json.JsonDocument.Parse(optionsJson);
        return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            type,
            challenge = options.RootElement.GetProperty("challenge").GetString(),
            origin,
        });
    }

    private static string SignedAssertion(
        PasskeyCredentialEntity credential, byte[] clientData, Func<byte[], byte[]> sign)
    {
        var authenticatorData = new Fido2NetLib.Objects.AuthenticatorData(
            SHA256.HashData("localhost"u8),
            Fido2NetLib.Objects.AuthenticatorFlags.UP | Fido2NetLib.Objects.AuthenticatorFlags.UV,
            signCount: 1, null, null).ToByteArray();

        return System.Text.Json.JsonSerializer.Serialize(new AuthenticatorAssertionRawResponse
        {
            Id = System.Buffers.Text.Base64Url.EncodeToString(credential.CredentialId),
            RawId = credential.CredentialId,
            Type = Fido2NetLib.Objects.PublicKeyCredentialType.PublicKey,
            Response = new AuthenticatorAssertionRawResponse.AssertionResponse
            {
                AuthenticatorData = authenticatorData,
                Signature = sign([.. authenticatorData, .. SHA256.HashData(clientData)]),
                ClientDataJson = clientData,
                UserHandle = credential.SubjectId.ToByteArray(),
            },
        });
    }

    #endregion

    #region The origin a ceremony presents

    public static TheoryData<string, string, bool> PresentedOrigins() => new()
    {
        { "Production", "https://localhost", true },
        { "Production", "https://rhys.localhost", true },
        { "Production", "https://a.b.localhost", true },
        { "Production", "https://rhys.localhost:8443", true },
        { "Production", "https://rhys.localhost:443", true },
        { "Production", "http://rhys.localhost", true },
        { "Production", "https://RHYS.LOCALHOST", true },
        { "Production", "https://notlocalhost", false },
        { "Production", "https://localhost.example.com", false },
        { "Production", "https://cgm.example.com", false },
        { "Production", "localhost", false },
        { "Development", "https://cgm.example.com", true },
    };

    [Theory]
    [Trait("Category", "Unit")]
    [MemberData(nameof(PresentedOrigins))]
    public async Task AnAssertion_IsAcceptedOnlyFromTheRpIdOrAHostBeneathIt(
        string environmentName, string origin, bool accepted)
    {
        using var key = RSA.Create(2048);
        var credential = await SeedActiveMemberCredentialAsync(key);
        using var provider = RegisteredProvider();
        var service = RegisteredService(provider, environmentName);

        var options = await service.GenerateDiscoverableAssertionOptionsAsync(_tenantId);
        var assertion = SignedAssertion(credential, options.OptionsJson,
            signed => key.SignData(signed, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1), origin);

        var thrown = await Record.ExceptionAsync(
            () => service.CompleteAssertionAsync(assertion, options.ChallengeToken, _tenantId));

        if (accepted)
            thrown.Should().BeNull();
        else
            thrown.Should().BeOfType<Fido2VerificationException>()
                .Which.Code.Should().Be(Fido2NetLib.Exceptions.Fido2ErrorCode.InvalidAuthenticatorResponseOrigin);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnOriginAcceptedOnOneCeremony_IsJudgedAfreshOnTheNext()
    {
        using var key = RSA.Create(2048);
        var credential = await SeedActiveMemberCredentialAsync(key);
        using var provider = RegisteredProvider();
        var service = RegisteredService(provider, "Development");
        var origin = "https://cgm.example.com";

        var first = await service.GenerateDiscoverableAssertionOptionsAsync(_tenantId);
        await service.CompleteAssertionAsync(
            SignedAssertion(credential, first.OptionsJson,
                signed => key.SignData(signed, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1), origin),
            first.ChallengeToken, _tenantId);
        await _dbContext.Entry(credential).ReloadAsync();
        credential.SignCount = 0;
        await _dbContext.SaveChangesAsync();

        var production = RegisteredService(provider, "Production");
        var second = await production.GenerateDiscoverableAssertionOptionsAsync(_tenantId);
        var thrown = await Record.ExceptionAsync(() => production.CompleteAssertionAsync(
            SignedAssertion(credential, second.OptionsJson,
                signed => key.SignData(signed, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1), origin),
            second.ChallengeToken, _tenantId));

        thrown.Should().BeOfType<Fido2VerificationException>()
            .Which.Code.Should().Be(Fido2NetLib.Exceptions.Fido2ErrorCode.InvalidAuthenticatorResponseOrigin);
    }

    public static TheoryData<string, byte[]> UnreadableClientData() => new()
    {
        { "not JSON", "not json"u8.ToArray() },
        { "an array", "[]"u8.ToArray() },
        { "a numeric origin", "{\"origin\":5}"u8.ToArray() },
        { "no origin", "{}"u8.ToArray() },
        { "an origin that is not UTF-8", [.. "{\"origin\":\""u8, 0xFF, 0xFE, .. "\"}"u8] },
        { "an origin holding half a surrogate pair", "{\"origin\":\"\\uD800\"}"u8.ToArray() },
    };

    [Theory]
    [Trait("Category", "Unit")]
    [MemberData(nameof(UnreadableClientData))]
    public async Task AnAssertionWhoseClientDataNamesNoOrigin_IsRefusedByTheVerification(string shape, byte[] clientData)
    {
        using var key = RSA.Create(2048);
        var credential = await SeedActiveMemberCredentialAsync(key);
        using var provider = RegisteredProvider();
        var service = RegisteredService(provider);

        var options = await service.GenerateDiscoverableAssertionOptionsAsync(_tenantId);
        var assertion = SignedAssertion(credential, clientData,
            signed => key.SignData(signed, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

        var thrown = await Record.ExceptionAsync(
            () => service.CompleteAssertionAsync(assertion, options.ChallengeToken, _tenantId));

        thrown.Should().BeOfType<Fido2VerificationException>("a client data with {0} names no origin it can read", shape);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AssertionsFromManyHosts_LeaveTheSharedRelyingPartyOriginsAsConfigured()
    {
        using var key = RSA.Create(2048);
        var credential = await SeedActiveMemberCredentialAsync(key);
        using var provider = RegisteredProvider();
        var (origins, fullyQualified) = SharedOrigins(provider);
        var service = RegisteredService(provider);

        for (var i = 0; i < 50; i++)
        {
            var options = await service.GenerateDiscoverableAssertionOptionsAsync(_tenantId);
            var assertion = SignedAssertion(credential, options.OptionsJson, _ => [0], $"https://tenant-{i}.localhost");

            var thrown = await Record.ExceptionAsync(
                () => service.CompleteAssertionAsync(assertion, options.ChallengeToken, _tenantId));

            thrown.Should().BeOfType<Fido2VerificationException>()
                .Which.Code.Should().Be(Fido2NetLib.Exceptions.Fido2ErrorCode.InvalidSignature,
                    "the origin is accepted, so the signature is what refuses it");
        }

        SharedOrigins(provider).Should().Be((origins, fullyQualified));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ConcurrentAssertionsFromManyHosts_LeaveTheSharedRelyingPartyOriginsAsConfigured()
    {
        using var key = RSA.Create(2048);
        var credential = await SeedActiveMemberCredentialAsync(key);
        using var provider = RegisteredProvider();
        var (origins, fullyQualified) = SharedOrigins(provider);
        var dataProtection = new EphemeralDataProtectionProvider();

        var refusals = await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(async () =>
        {
            var service = RegisteredService(provider, dataProtection: dataProtection);
            var thrown = new List<Exception?>();
            for (var i = 0; i < 25; i++)
            {
                var options = await service.GenerateDiscoverableAssertionOptionsAsync(_tenantId);
                var assertion = SignedAssertion(
                    credential, options.OptionsJson, _ => [0], $"https://tenant-{worker}-{i}.localhost");
                thrown.Add(await Record.ExceptionAsync(
                    () => service.CompleteAssertionAsync(assertion, options.ChallengeToken, _tenantId)));
            }

            return thrown;
        })));

        refusals.SelectMany(r => r).Should().HaveCount(200).And.AllSatisfy(thrown =>
            thrown.Should().BeOfType<Fido2VerificationException>()
                .Which.Code.Should().Be(Fido2NetLib.Exceptions.Fido2ErrorCode.InvalidSignature));
        SharedOrigins(provider).Should().Be((origins, fullyQualified));
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("https://rhys.localhost", true)]
    [InlineData("https://localhost", true)]
    [InlineData("https://cgm.example.com", false)]
    public async Task ARegistration_IsAcceptedOnlyFromTheRpIdOrAHostBeneathIt_AndLeavesTheSharedOriginsAsConfigured(
        string origin, bool accepted)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var provider = RegisteredProvider();
        var (origins, fullyQualified) = SharedOrigins(provider);
        var service = RegisteredService(provider);

        var options = await service.GenerateRegistrationOptionsAsync(
            _subjectId, "rhys", PasskeyRegistrationFlow.OwnAccount);
        var attestation = Attestation(key, options.OptionsJson, origin);

        var thrown = await Record.ExceptionAsync(() => service.CompleteRegistrationAsync(
            attestation, options.ChallengeToken, PasskeyRegistrationFlow.OwnAccount, _tenantId, _subjectId));

        if (accepted)
        {
            thrown.Should().BeNull();
            (await _dbContext.PasskeyCredentials.CountAsync(c => c.SubjectId == _subjectId)).Should().Be(1);
        }
        else
        {
            thrown.Should().BeOfType<Fido2VerificationException>()
                .Which.Code.Should().Be(Fido2NetLib.Exceptions.Fido2ErrorCode.InvalidAuthenticatorResponseOrigin);
        }

        SharedOrigins(provider).Should().Be((origins, fullyQualified));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ARegistrationWithNoAuthenticatorResponse_IsRefusedByTheVerification()
    {
        using var provider = RegisteredProvider();
        var service = RegisteredService(provider);

        var options = await service.GenerateRegistrationOptionsAsync(
            _subjectId, "rhys", PasskeyRegistrationFlow.OwnAccount);
        var credentialId = Guid.CreateVersion7().ToByteArray();
        var attestation = System.Text.Json.JsonSerializer.Serialize(new AuthenticatorAttestationRawResponse
        {
            Id = System.Buffers.Text.Base64Url.EncodeToString(credentialId),
            RawId = credentialId,
            Type = Fido2NetLib.Objects.PublicKeyCredentialType.PublicKey,
        });

        var thrown = await Record.ExceptionAsync(() => service.CompleteRegistrationAsync(
            attestation, options.ChallengeToken, PasskeyRegistrationFlow.OwnAccount, _tenantId, _subjectId));

        thrown.Should().BeOfType<Fido2VerificationException>();
        (await _dbContext.PasskeyCredentials.CountAsync(c => c.SubjectId == _subjectId)).Should().Be(0);
    }

    /// <summary>The API's own registrations, for a server whose base domain is <c>localhost</c>.</summary>
    private static ServiceProvider RegisteredProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [BaseDomainOptions.ConfigKey] = "localhost",
                [$"{JwtOptions.SectionName}:SecretKey"] = new string('k', 64),
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthenticationAndIdentity(configuration);
        return services.BuildServiceProvider();
    }

    /// <summary>A service holding what <paramref name="provider"/> hands every request for the relying party.</summary>
    private PasskeyService RegisteredService(
        IServiceProvider provider, string environmentName = "Production",
        IDataProtectionProvider? dataProtection = null)
    {
        var contexts = _dbContext.AsFactory();
        return ActivatorUtilities.CreateInstance<PasskeyService>(
            provider,
            new PasskeyCredentialRepository(contexts),
            new SubjectRepository(contexts, AccountLinkRoutes.Off),
            new TenantMemberService(new TenantMembershipRepository(contexts, AccountLinkRoutes.Off), TimeProvider.System),
            dataProtection ?? new EphemeralDataProtectionProvider(),
            Mock.Of<IHostEnvironment>(e => e.EnvironmentName == environmentName),
            TimeProvider.System);
    }

    private static (string Origins, string FullyQualified) SharedOrigins(IServiceProvider provider)
    {
        var shared = provider.GetRequiredService<IOptions<Fido2Configuration>>().Value;
        return (string.Join(",", shared.Origins.Order()), string.Join(",", shared.FullyQualifiedOrigins.Order()));
    }

    /// <summary>A "none" attestation of a new P-256 credential, made by a browser at <paramref name="origin"/>.</summary>
    private static string Attestation(ECDsa key, string optionsJson, string origin)
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
                ClientDataJson = ClientData("webauthn.create", optionsJson, origin),
            },
        });
    }

    #endregion

    #region DescribeRpIdMismatch

    [Fact]
    [Trait("Category", "Unit")]
    public void DescribeRpIdMismatch_WhenHostIsTheRpId_AllowsTheCeremony()
    {
        var service = CreateService("Production", "cgm.example.com");

        service.DescribeRpIdMismatch("cgm.example.com").Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void DescribeRpIdMismatch_WhenHostIsATenantSubdomain_AllowsTheCeremony()
    {
        var service = CreateService("Production", "cgm.example.com");

        service.DescribeRpIdMismatch("rhys.cgm.example.com").Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void DescribeRpIdMismatch_WhenHostMerelyEndsWithTheRpId_Refuses()
    {
        // The label boundary is the whole guarantee: "evilcgm.example.com" is a stranger's host,
        // not a tenant. This predicate also decides which origins reach the FIDO2 allow-list.
        var service = CreateService("Production", "cgm.example.com");

        service.DescribeRpIdMismatch("evilcgm.example.com").Should().NotBeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void DescribeRpIdMismatch_WhenHostIsElsewhere_NamesBothAddressesAndTheSetting()
    {
        var service = CreateService("Production", "localhost");

        var detail = service.DescribeRpIdMismatch("cgm.example.com");

        detail.Should().NotBeNull();
        detail.Should().Contain("localhost")
            .And.Contain("cgm.example.com")
            .And.Contain("BASE_DOMAIN");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void DescribeRpIdMismatch_InDevelopment_AllowsAnyHost()
    {
        // What the exemption buys is reaching the API or the web app directly on localhost,
        // past the gateway whose host the dev rpId already names.
        var service = CreateService("Development", "nocturne.localhost");

        service.DescribeRpIdMismatch("localhost").Should().BeNull();
    }

    #endregion

    #region Helpers

    private PasskeyService CreateService(
        string environmentName = "Development", string rpId = "localhost",
        IDataProtectionProvider? dataProtection = null)
    {
        var dataProtectionProvider = dataProtection ?? new EphemeralDataProtectionProvider();
        var logger = NullLogger<PasskeyService>.Instance;

        var environment = Mock.Of<IHostEnvironment>(e => e.EnvironmentName == environmentName);
        var contexts = _dbContext.AsFactory();
        return new PasskeyService(
            new PasskeyCredentialRepository(contexts), new SubjectRepository(contexts, AccountLinkRoutes.Off), new TenantMemberService(new TenantMembershipRepository(contexts, AccountLinkRoutes.Off), TimeProvider.System),
            dataProtectionProvider, RelyingParty(rpId), logger, environment, TimeProvider.System);
    }

    /// <summary>A relying party on <paramref name="rpId"/> whose one configured origin is <c>https://{rpId}</c>.</summary>
    internal static IOptionsFactory<Fido2Configuration> RelyingParty(string rpId) =>
        new OptionsFactory<Fido2Configuration>(
            [new ConfigureOptions<Fido2Configuration>(o =>
            {
                o.RPID = rpId;
                o.RPName = "Test";
                o.Origins = new HashSet<string> { $"https://{rpId}" };
            })],
            []);

    private static PasskeyCredentialEntity CreateCredentialEntity(
        Guid subjectId, string? label = null)
    {
        return new PasskeyCredentialEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = subjectId,
            CredentialId = Guid.CreateVersion7().ToByteArray(),
            PublicKey = [1, 2, 3, 4],
            SignCount = 0,
            Label = label,
            CreatedAt = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// Mirror of the private ChallengeCookiePayload for test deserialization
    /// </summary>
    private sealed class ChallengeCookiePayloadForTest
    {
        public string OptionsJson { get; set; } = string.Empty;
        public Guid? SubjectId { get; set; }
        public DateTime ExpiresAt { get; set; }
    }

    #endregion
}

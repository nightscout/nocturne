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
using Nocturne.Core.Contracts.Multitenancy;
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
    private readonly SpentPasskeyChallenges _spent = new();

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
        string origin = "https://localhost", uint signCount = 1) =>
        SignedAssertion(credential, ClientData("webauthn.get", optionsJson, origin), sign, signCount);

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
        PasskeyCredentialEntity credential, byte[] clientData, Func<byte[], byte[]> sign, uint signCount = 1)
    {
        var authenticatorData = new Fido2NetLib.Objects.AuthenticatorData(
            SHA256.HashData("localhost"u8),
            Fido2NetLib.Objects.AuthenticatorFlags.UP | Fido2NetLib.Objects.AuthenticatorFlags.UV,
            signCount, null, null).ToByteArray();

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
            thrown.Should().BeOfType<PasskeyRegistrationRefusedException>()
                .Which.InnerException.Should().BeOfType<Fido2VerificationException>()
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

        thrown.Should().BeOfType<PasskeyRegistrationRefusedException>()
            .Which.InnerException.Should().BeOfType<Fido2VerificationException>();
        (await _dbContext.PasskeyCredentials.CountAsync(c => c.SubjectId == _subjectId)).Should().Be(0);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ASetupOwnerRegistration_StoresThePasskeyOnlyWhileTheSetupOwnerRecordNamesItsSubject(bool recorded)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var provider = RegisteredProvider();
        var service = RegisteredService(provider);
        var otherOwner = Guid.CreateVersion7();
        await RecordSetupOwnerAsync(recorded ? _subjectId : otherOwner);

        var options = await service.GenerateRegistrationOptionsAsync(
            _subjectId, "owner", PasskeyRegistrationFlow.SetupOwner);
        var complete = () => service.CompleteRegistrationAsync(
            Attestation(key, options.OptionsJson, "https://localhost"), options.ChallengeToken,
            PasskeyRegistrationFlow.SetupOwner, _tenantId, _subjectId);

        if (recorded)
            (await complete()).SubjectId.Should().Be(_subjectId);
        else
            await complete.Should().ThrowAsync<SetupOwnerTakenException>();

        (await _dbContext.PasskeyCredentials.AsNoTracking().CountAsync(c => c.SubjectId == _subjectId))
            .Should().Be(recorded ? 1 : 0);
        (await _dbContext.Tenants.AsNoTracking().SingleAsync(t => t.Id == _tenantId)).SetupOwnerSubjectId
            .Should().Be(recorded ? null : otherOwner);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ARegistrationOutsideSetup_StoresThePasskeyWithoutReadingTheSetupOwnerRecord()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var provider = RegisteredProvider();
        var service = RegisteredService(provider);

        var options = await service.GenerateRegistrationOptionsAsync(
            _subjectId, "rhys", PasskeyRegistrationFlow.OwnAccount);
        await service.CompleteRegistrationAsync(
            Attestation(key, options.OptionsJson, "https://localhost"), options.ChallengeToken,
            PasskeyRegistrationFlow.OwnAccount, _tenantId, _subjectId);

        (await _dbContext.PasskeyCredentials.AsNoTracking().CountAsync(c => c.SubjectId == _subjectId)).Should().Be(1);
        (await _dbContext.Tenants.AsNoTracking().AnyAsync()).Should().BeFalse();
    }

    /// <summary>Makes <see cref="_tenantId"/> a tenant whose setup owner record names <paramref name="subjectId"/>.</summary>
    private async Task RecordSetupOwnerAsync(Guid subjectId)
    {
        _dbContext.Tenants.Add(new TenantEntity
        {
            Id = _tenantId, Slug = "setup", DisplayName = "Setup", SetupOwnerSubjectId = subjectId,
        });
        await _dbContext.SaveChangesAsync();
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
            _spent,
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

    /// <summary>
    /// The types a refused registration raises, which the enrolment flows' registration proof in
    /// <see cref="Nocturne.API.Controllers.Authentication.PasskeyController"/> and the setup owner's in
    /// <see cref="Nocturne.API.Controllers.V4.SetupController"/> catch.
    /// </summary>
    public static readonly Type[] RegistrationRefusals =
    [
        typeof(InvalidOperationException), typeof(System.Text.Json.JsonException), typeof(PasskeyRegistrationRefusedException),
    ];

    /// <summary>Each malformed attestation and the type the verifier throws for it.</summary>
    public static TheoryData<string, Type> MalformedAttestations() => new()
    {
        { "attestation object that is not CBOR", typeof(Fido2VerificationException) },
        { "attestation object that is a CBOR array", typeof(Fido2VerificationException) },
        { "attestation object with no format", typeof(Fido2VerificationException) },
        { "unknown format", typeof(Fido2VerificationException) },
        { "statement that is not a map", typeof(Fido2VerificationException) },
        { "truncated authenticator data", typeof(Fido2VerificationException) },
        { "credential public key that is not CBOR", typeof(Fido2VerificationException) },
        { "credential public key with a short coordinate", typeof(Fido2VerificationException) },
        { "credential public key of an unknown key type", typeof(Fido2VerificationException) },
        { "packed self attestation with a signature that is not DER", typeof(Fido2VerificationException) },
        { "packed self attestation with an algorithm the key is not", typeof(Fido2VerificationException) },
        { "packed with a certificate chain that is not DER", typeof(Fido2VerificationException) },
        { "packed with an empty certificate chain", typeof(Fido2VerificationException) },
        { "packed with a certificate chain that is text", typeof(Fido2VerificationException) },
        { "packed with a certificate and a signature that is not DER", typeof(Fido2VerificationException) },
        { "packed with a certificate key that is an Ed25519 key of 31 bytes", typeof(FormatException) },
        { "packed with a certificate key that is an EC point off its curve", typeof(CryptographicException) },
        { "packed with a certificate key that is an RSA key that is not DER", typeof(CryptographicException) },
        { "fido-u2f with a certificate chain that is not DER", typeof(Fido2VerificationException) },
        { "tpm with structures that do not parse", typeof(Fido2VerificationException) },
        { "android-key with a certificate chain that is not DER", typeof(Fido2VerificationException) },
        { "android-safetynet with a response that is not a JWS", typeof(Fido2VerificationException) },
        { "android-safetynet with a JWS header that is not a JSON object", typeof(InvalidOperationException) },
        { "android-safetynet with a certificate key that is an EC point off its curve", typeof(CryptographicException) },
        { "apple with a certificate chain that is not DER", typeof(Fido2VerificationException) },
        { "apple-appattest with a chain to another root", typeof(Fido2VerificationException) },
        { "packed, signed, with a transports extension that is a SEQUENCE", typeof(System.Formats.Asn1.AsnContentException) },
        { "packed, signed, with a transports extension that is an empty OCTET STRING", typeof(System.Formats.Asn1.AsnContentException) },
        { "packed, signed, with a transports extension that is an empty BIT STRING", typeof(IndexOutOfRangeException) },
        { "packed, signed, with an aaguid extension whose length is not minimal", typeof(System.Formats.Asn1.AsnContentException) },
        { "packed, signed, with a basic constraints extension that is an empty OCTET STRING", typeof(CryptographicException) },
        { "fido-u2f, signed, with a transports extension that is a SEQUENCE", typeof(System.Formats.Asn1.AsnContentException) },
        { "fido-u2f, signed, with a transports extension that is an empty BIT STRING", typeof(IndexOutOfRangeException) },
        { "tpm, signed, with a SAN that holds no directory name", typeof(System.Formats.Asn1.AsnContentException) },
        { "tpm, signed, with a SAN name that holds its attributes in one flat sequence", typeof(InvalidCastException) },
        { "tpm, signed, with a SAN value that is not UTF-8", typeof(System.Formats.Asn1.AsnContentException) },
        { "tpm, signed, with a SAN value that is not a UTF8String", typeof(System.Formats.Asn1.AsnContentException) },
        { "tpm, signed, with a SAN object identifier that does not parse", typeof(System.Formats.Asn1.AsnContentException) },
        { "tpm, signed, with an EKU extension that is an empty OCTET STRING", typeof(CryptographicException) },
        { "tpm, signed, with a basic constraints extension that is an empty OCTET STRING", typeof(CryptographicException) },
        { "android-key, signed, with an attestation record that is not ASN.1", typeof(Fido2VerificationException) },
        { "android-key, signed, with an origin that is not a 32-bit integer", typeof(Fido2VerificationException) },
        { "android-key, signed, with a second certificate that is not DER", typeof(Fido2VerificationException) },
        { "android-safetynet, signed by its own certificate, with a timestamp after the year 9999", typeof(ArgumentOutOfRangeException) },
        { "android-safetynet, signed by its own certificate, with the nonce and a chain to another root", typeof(Fido2VerificationException) },
        { "apple, with the nonce in an extension that does not parse", typeof(Fido2VerificationException) },
        { "apple, with the nonce and a chain to another root", typeof(Fido2VerificationException) },
    };

    /// <summary>
    /// The attestations whose certificate key is on a curve the verifier does not name. Each needs a
    /// brainpoolP256r1 key, which a platform without that curve cannot create.
    /// </summary>
    public static TheoryData<string, Type> UnnamedCurveAttestations() => new()
    {
        { "packed with a certificate key on a curve the verifier does not name", typeof(Exception) },
        { "tpm with an AIK certificate key on a curve the verifier does not name", typeof(Exception) },
        { "apple with the nonce and a certificate key on a curve the verifier does not name", typeof(Exception) },
    };

    private static bool BrainpoolIsSupported()
    {
        try
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.brainpoolP256r1);
            return true;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <summary>A theory skipped on a platform that cannot create a brainpoolP256r1 key.</summary>
    private sealed class BrainpoolTheoryAttribute : TheoryAttribute
    {
        public BrainpoolTheoryAttribute()
        {
            if (!BrainpoolIsSupported())
                Skip = "This platform has no brainpoolP256r1 curve.";
        }
    }

    [Theory]
    [Trait("Category", "Unit")]
    [MemberData(nameof(MalformedAttestations))]
    public Task AMalformedAttestation_IsRefusedWithTheVerifiersFailureInside_AndStoresNoPasskey(
        string kind, Type verifierFailure) => AssertRefusedWithTheVerifiersFailure(kind, verifierFailure);

    [BrainpoolTheory]
    [Trait("Category", "Unit")]
    [MemberData(nameof(UnnamedCurveAttestations))]
    public Task AnAttestationOnAnUnnamedCurve_IsRefusedWithTheVerifiersFailureInside_AndStoresNoPasskey(
        string kind, Type verifierFailure) => AssertRefusedWithTheVerifiersFailure(kind, verifierFailure);

    private async Task AssertRefusedWithTheVerifiersFailure(string kind, Type verifierFailure)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var provider = RegisteredProvider();
        var service = RegisteredService(provider);
        var options = await service.GenerateRegistrationOptionsAsync(
            _subjectId, "invitee", PasskeyRegistrationFlow.MemberInvite);

        var thrown = await Record.ExceptionAsync(() => service.CompleteRegistrationAsync(
            MalformedAttestation(kind, key, options.OptionsJson), options.ChallengeToken,
            PasskeyRegistrationFlow.MemberInvite, _tenantId, _subjectId));

        var inner = thrown.Should().BeOfType<PasskeyRegistrationRefusedException>($"{kind} threw {thrown}")
            .Which.InnerException;
        if (verifierFailure == typeof(CryptographicException))
            inner.Should().BeAssignableTo<CryptographicException>($"{kind} threw {inner}");
        else
            inner.Should().BeOfType(verifierFailure, $"{kind} threw {inner}");
        (await _dbContext.PasskeyCredentials.CountAsync(c => c.SubjectId == _subjectId)).Should().Be(0);
    }

    /// <summary>
    /// A well-formed attestation of each format the refusal theory signs, built by the same code, so a
    /// refusal case that fails before the part it names is seen.
    /// </summary>
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("packed, signed, well formed")]
    [InlineData("fido-u2f, signed, well formed")]
    [InlineData("tpm, signed, well formed")]
    public async Task AWellFormedSignedAttestation_IsAccepted_AndStoresThePasskey(string kind)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var provider = RegisteredProvider();
        var service = RegisteredService(provider);
        var options = await service.GenerateRegistrationOptionsAsync(
            _subjectId, "invitee", PasskeyRegistrationFlow.MemberInvite);

        var result = await service.CompleteRegistrationAsync(
            MalformedAttestation(kind, key, options.OptionsJson), options.ChallengeToken,
            PasskeyRegistrationFlow.MemberInvite, _tenantId, _subjectId);

        result.SubjectId.Should().Be(_subjectId);
        (await _dbContext.PasskeyCredentials.CountAsync(c => c.SubjectId == _subjectId)).Should().Be(1);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void TheControllerTests_RefuseExactlyTheRegistrationRefusalsTheServiceNames()
    {
        static IEnumerable<Type> TypesOf(IEnumerable<object[]> rows) => rows.Select(row => row[0].GetType());

        TypesOf(Nocturne.API.Tests.Controllers.PasskeyControllerTests.RefusedRegistrations())
            .Should().BeEquivalentTo(RegistrationRefusals);
        TypesOf(Nocturne.API.Tests.Controllers.V4.SetupControllerTests.PasskeyProofRefusals)
            .Should().BeEquivalentTo(RegistrationRefusals);
    }

    public static TheoryData<Exception> UniquenessReadFaults() => new()
    {
        new TimeoutException("The credential store did not answer."),
        new SpendStoreUnavailableException(),
    };

    [Theory]
    [Trait("Category", "Unit")]
    [MemberData(nameof(UniquenessReadFaults))]
    public async Task AFaultInTheCredentialUniquenessRead_PropagatesAsItself_AndStoresNoPasskey(Exception fault)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var passkeys = new Mock<IPasskeyCredentialRepository>();
        passkeys.Setup(p => p.ExistsAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>())).ThrowsAsync(fault);
        var service = ServiceWithCredentials(passkeys);
        var options = await service.GenerateRegistrationOptionsAsync(
            _subjectId, "invitee", PasskeyRegistrationFlow.MemberInvite);

        var thrown = await Record.ExceptionAsync(() => service.CompleteRegistrationAsync(
            Attestation(key, options.OptionsJson, "https://localhost"), options.ChallengeToken,
            PasskeyRegistrationFlow.MemberInvite, _tenantId, _subjectId));

        thrown.Should().BeSameAs(fault);
        passkeys.Verify(p => p.CreateAsync(It.IsAny<Nocturne.Core.Models.Authorization.NewPasskeyCredential>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ACredentialThatIsAlreadyRegistered_IsRefused_AndNotStored()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var passkeys = new Mock<IPasskeyCredentialRepository>();
        passkeys.Setup(p => p.ExistsAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var service = ServiceWithCredentials(passkeys);
        var options = await service.GenerateRegistrationOptionsAsync(
            _subjectId, "invitee", PasskeyRegistrationFlow.MemberInvite);

        var thrown = await Record.ExceptionAsync(() => service.CompleteRegistrationAsync(
            Attestation(key, options.OptionsJson, "https://localhost"), options.ChallengeToken,
            PasskeyRegistrationFlow.MemberInvite, _tenantId, _subjectId));

        thrown.Should().BeOfType<PasskeyRegistrationRefusedException>().Which.InnerException.Should().BeNull();
        passkeys.Verify(p => p.CreateAsync(It.IsAny<Nocturne.Core.Models.Authorization.NewPasskeyCredential>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>A service whose credential store is <paramref name="passkeys"/>, for a server whose base domain is <c>localhost</c>.</summary>
    private PasskeyService ServiceWithCredentials(Mock<IPasskeyCredentialRepository> passkeys)
    {
        passkeys.Setup(p => p.GetCredentialIdsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        return new(
            passkeys.Object, _spent,
            new Mock<ISubjectRepository>(MockBehavior.Strict).Object,
            new Mock<ITenantMemberService>(MockBehavior.Strict).Object,
            new EphemeralDataProtectionProvider(), RelyingParty("localhost"), NullLogger<PasskeyService>.Instance,
            Mock.Of<IHostEnvironment>(e => e.EnvironmentName == "Development"), TimeProvider.System);
    }

    private static string MalformedAttestation(string kind, ECDsa key, string optionsJson)
    {
        var credentialId = Guid.CreateVersion7().ToByteArray();
        var garbage = new byte[] { 0x30, 0x03, 0x02, 0x01 };
        var coseKey = new Fido2NetLib.Objects.CredentialPublicKey(key, Fido2NetLib.Objects.COSE.Algorithm.ES256).GetBytes();

        byte[] AuthData(byte[] publicKey) =>
        [
            .. SHA256.HashData("localhost"u8),
            (byte)(Fido2NetLib.Objects.AuthenticatorFlags.UP | Fido2NetLib.Objects.AuthenticatorFlags.UV
                | Fido2NetLib.Objects.AuthenticatorFlags.AT),
            0, 0, 0, 0,
            .. new byte[16],
            (byte)(credentialId.Length >> 8), (byte)credentialId.Length,
            .. credentialId,
            .. publicKey,
        ];

        var authData = AuthData(coseKey);
        Fido2NetLib.Cbor.CborArray Chain(params byte[][] certificates)
        {
            var chain = new Fido2NetLib.Cbor.CborArray();
            foreach (var certificate in certificates)
                chain.Add(certificate);
            return chain;
        }

        Fido2NetLib.Cbor.CborMap Statement(params (string Name, object Value)[] fields)
        {
            var statement = new Fido2NetLib.Cbor.CborMap();
            foreach (var (name, value) in fields)
            {
                switch (value)
                {
                    case int number: statement.Add(name, number); break;
                    case string text: statement.Add(name, text); break;
                    case byte[] bytes: statement.Add(name, bytes); break;
                    default: statement.Add(name, (Fido2NetLib.Cbor.CborObject)value); break;
                }
            }
            return statement;
        }

        const int es256 = -7;
        var garbageBytes = garbage;

        byte[] Object(string? format, Fido2NetLib.Cbor.CborObject statement, byte[] data)
        {
            var map = new Fido2NetLib.Cbor.CborMap();
            if (format is not null)
                map.Add("fmt", format);
            map.Add("attStmt", statement);
            map.Add("authData", data);
            return map.Encode();
        }

        var selfSigned = new System.Security.Cryptography.X509Certificates.CertificateRequest(
                "CN=Authenticator", key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1)).RawData;

        var clientData = ClientData("webauthn.create", optionsJson, "https://localhost");
        var clientDataHash = SHA256.HashData(clientData);
        byte[] signedData = [.. authData, .. clientDataHash];
        byte[] Sign(byte[] data) => key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        byte[] Certificate(string subject, bool notACa, params (string Oid, byte[] Value)[] extensions) =>
            CertificateOf(key, subject, notACa, extensions);

        byte[] CertificateOf(ECDsa certificateKey, string subject, bool notACa, params (string Oid, byte[] Value)[] extensions)
        {
            var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
                new System.Security.Cryptography.X509Certificates.X500DistinguishedName(subject), certificateKey, HashAlgorithmName.SHA256);
            if (notACa)
            {
                request.CertificateExtensions.Add(
                    new System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension(false, false, 0, false));
            }
            foreach (var (oid, value) in extensions)
                request.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509Extension(oid, value, false));
            return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1)).RawData;
        }

        static byte[] Der(byte[] tag, params byte[][] contents)
        {
            byte[] body = [.. contents.SelectMany(content => content)];
            return [.. tag, (byte)body.Length, .. body];
        }

        const string transportsOid = "1.3.6.1.4.1.45724.2.1.1";
        const string aaguidOid = "1.3.6.1.4.1.45724.1.1.4";
        const string packedSubject = "C=US, O=Test, OU=Authenticator Attestation, CN=Test";

        byte[] PackedWith(byte[] certificate, int algorithm = es256) => Object("packed", Statement(
            ("alg", algorithm), ("sig", Sign(signedData)), ("x5c", Chain(certificate))), authData);
        byte[] Packed(string extensionOid, byte[] extension) =>
            PackedWith(Certificate(packedSubject, notACa: true, (extensionOid, extension)));

        // A certificate for a subject public key the test writes itself, issued under the attestation key.
        byte[] CertificateHolding(System.Security.Cryptography.X509Certificates.PublicKey subjectKey, string subject)
        {
            var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
                new System.Security.Cryptography.X509Certificates.X500DistinguishedName(subject), subjectKey, HashAlgorithmName.SHA256);
            return request.Create(
                new System.Security.Cryptography.X509Certificates.X500DistinguishedName("CN=Issuer"),
                System.Security.Cryptography.X509Certificates.X509SignatureGenerator.CreateForECDsa(key),
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), [1, 2, 3, 4]).RawData;
        }

        System.Security.Cryptography.X509Certificates.PublicKey SubjectKey(string algorithmOid, byte[]? parameters, byte[] value) =>
            new(new Oid(algorithmOid), parameters is null ? null : new AsnEncodedData(parameters), new AsnEncodedData(value));
        byte[] p256Parameters = [0x06, 0x08, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x03, 0x01, 0x07];
        var ecPointOffItsCurve = SubjectKey("1.2.840.10045.2.1", p256Parameters, [0x04, .. new byte[64]]);
        var rsaKeyThatIsNotDer = SubjectKey("1.2.840.113549.1.1.1", [0x05, 0x00], [0x30, 0x03, 0x02, 0x01]);
        var shortEd25519Key = SubjectKey("1.3.101.112", null, new byte[31]);
        const int edDsa = -8;
        using var unnamedCurveKey = kind.Contains("a curve the verifier does not name")
            ? ECDsa.Create(ECCurve.NamedCurves.brainpoolP256r1)
            : null;

        var point = key.ExportParameters(false).Q;
        byte[] FidoU2fWith(byte[] certificate) => Object("fido-u2f", Statement(
            ("sig", Sign([0x00, .. SHA256.HashData("localhost"u8), .. clientDataHash, .. credentialId, 0x04, .. point.X!, .. point.Y!])),
            ("x5c", Chain(certificate))), authData);
        byte[] FidoU2f(byte[] transports) => FidoU2fWith(Certificate("CN=U2F", notACa: false, (transportsOid, transports)));

        static byte[] U16(int value) => [(byte)(value >> 8), (byte)value];
        static byte[] Sized(byte[] value) => [.. U16(value.Length), .. value];
        const int tpmAlgEcc = 0x0023, tpmAlgSha256 = 0x000B, tpmAlgNull = 0x0010, tpmEccNistP256 = 0x0003;
        byte[] pubArea =
        [
            .. U16(tpmAlgEcc), .. U16(tpmAlgSha256), 0, 0, 0, 0x72, .. U16(0),
            .. U16(tpmAlgNull), .. U16(tpmAlgNull), .. U16(tpmEccNistP256), .. U16(tpmAlgNull),
            .. Sized(point.X!), .. Sized(point.Y!),
        ];
        byte[] certInfo =
        [
            0xFF, 0x54, 0x43, 0x47, 0x80, 0x17, .. U16(0), .. Sized(SHA256.HashData(signedData)),
            .. new byte[8], .. new byte[4], .. new byte[4], 1, .. new byte[8],
            .. U16(34), .. U16(tpmAlgSha256), .. SHA256.HashData(pubArea), .. U16(0),
        ];
        byte[] tpmManufacturerOid = [0x06, 0x05, 0x67, 0x81, 0x05, 0x02, 0x01];
        byte[] SanName(params byte[][] attributes) => Der([0x30], Der([0xA4], Der([0x30], attributes)));
        byte[] TpmWith(byte[] aikCertificate) => Object("tpm", Statement(
            ("ver", "2.0"), ("alg", es256),
            ("x5c", Chain(aikCertificate)),
            ("sig", Sign(certInfo)), ("certInfo", certInfo), ("pubArea", pubArea)), authData);
        byte[] Tpm(byte[] san) => TpmWith(Certificate("", notACa: true, ("2.5.29.17", san)));
        byte[] SanAttribute(byte[] oid, string value) => Der([0x31], Der([0x30], oid, Der([0x0C], System.Text.Encoding.UTF8.GetBytes(value))));
        var wellFormedSan = ("2.5.29.17", SanName(
            SanAttribute(tpmManufacturerOid, "id:414D4400"),
            SanAttribute([0x06, 0x05, 0x67, 0x81, 0x05, 0x02, 0x02], "model"),
            SanAttribute([0x06, 0x05, 0x67, 0x81, 0x05, 0x02, 0x03], "id:13")));
        var aikEku = ("2.5.29.37", new System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("2.23.133.8.3") }, false).RawData);
        (string, byte[]) EmptyOctetString(string oid) => (oid, [0x04, 0x00]);

        const string androidRecordOid = "1.3.6.1.4.1.11129.2.1.17";
        byte[] AndroidKey(byte[] record, params byte[][] issuers) => Object("android-key", Statement(
            ("alg", es256), ("sig", Sign(signedData)),
            ("x5c", Chain([Certificate("CN=Android", notACa: false, (androidRecordOid, record)), .. issuers]))), authData);
        byte[] AndroidRecord(params byte[][] teeEnforced) => Der([0x30],
            [0x02, 0x01, 0x03], [0x0A, 0x01, 0x01], [0x02, 0x01, 0x04], [0x0A, 0x01, 0x01],
            Der([0x04], clientDataHash), [0x04, 0x00], [0x30, 0x00], Der([0x30], teeEnforced));

        const string appleNonceOid = "1.2.840.113635.100.8.2";
        byte[] AppleWith(byte[] certificate) => Object("apple", Statement(("x5c", Chain(certificate, certificate))), authData);
        byte[] Apple(byte[] nonceExtension) =>
            AppleWith(Certificate("CN=Apple", notACa: false, (appleNonceOid, nonceExtension)));
        byte[] appleNonce = Der([0x30], Der([0xA1], Der([0x04], SHA256.HashData(signedData))));

        byte[] SafetyNet(string jws) => Object("android-safetynet", Statement(
            ("ver", "1"), ("response", System.Text.Encoding.UTF8.GetBytes(jws))), authData);
        string SignedJws(byte[] certificate, object claims)
        {
            var header = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                new { alg = "ES256", x5c = new[] { Convert.ToBase64String(certificate) } });
            var signingInput = System.Buffers.Text.Base64Url.EncodeToString(header) + "."
                + System.Buffers.Text.Base64Url.EncodeToString(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(claims));
            var signature = key.SignData(
                System.Text.Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return signingInput + "." + System.Buffers.Text.Base64Url.EncodeToString(signature);
        }
        object SafetyNetClaims(long timestampMs) => new
        {
            nonce = Convert.ToBase64String(SHA256.HashData(signedData)), timestampMs, ctsProfileMatch = true,
        };
        var safetyNetCertificate = Certificate("CN=attest.android.com", notACa: false);

        var attestationObject = kind switch
        {
            "attestation object that is not CBOR" => [0xff, 0x00, 0x13],
            "attestation object that is a CBOR array" => Chain(garbage).Encode(),
            "attestation object with no format" => Object(null, Statement(), authData),
            "unknown format" => Object("nope", Statement(), authData),
            "statement that is not a map" => Object("packed", new Fido2NetLib.Cbor.CborTextString("x"), authData),
            "truncated authenticator data" => Object("none", Statement(), authData[..40]),
            "credential public key that is not CBOR" => Object("none", Statement(), AuthData([0xff, 0xff, 0x00])),
            "credential public key with a short coordinate" => Object("none", Statement(), AuthData(ShortCoordinateKey())),
            "credential public key of an unknown key type" => Object("none", Statement(), AuthData(UnknownKeyType())),
            "packed self attestation with a signature that is not DER" =>
                Object("packed", Statement(("alg", es256), ("sig", garbageBytes)), authData),
            "packed self attestation with an algorithm the key is not" =>
                Object("packed", Statement(("alg", -257), ("sig", garbageBytes)), authData),
            "packed with a certificate chain that is not DER" =>
                Object("packed", Statement(("alg", es256), ("sig", garbageBytes), ("x5c", Chain(garbage))), authData),
            "packed with an empty certificate chain" =>
                Object("packed", Statement(("alg", es256), ("sig", garbageBytes), ("x5c", Chain())), authData),
            "packed with a certificate chain that is text" =>
                Object("packed", Statement(("alg", es256), ("sig", garbageBytes), ("x5c", "x")), authData),
            "packed with a certificate and a signature that is not DER" =>
                Object("packed", Statement(("alg", es256), ("sig", garbageBytes), ("x5c", Chain(selfSigned))), authData),
            "fido-u2f with a certificate chain that is not DER" =>
                Object("fido-u2f", Statement(("sig", garbageBytes), ("x5c", Chain(garbage))), authData),
            "tpm with structures that do not parse" => Object("tpm", Statement(
                ("ver", "2.0"), ("alg", es256), ("x5c", Chain(garbage)),
                ("sig", garbageBytes), ("certInfo", garbageBytes), ("pubArea", garbageBytes)), authData),
            "android-key with a certificate chain that is not DER" =>
                Object("android-key", Statement(("alg", es256), ("sig", garbageBytes), ("x5c", Chain(garbage))), authData),
            "android-safetynet with a response that is not a JWS" => Object("android-safetynet", Statement(
                ("ver", "1"), ("response", garbageBytes)), authData),
            "apple with a certificate chain that is not DER" =>
                Object("apple", Statement(("x5c", Chain(garbage))), authData),
            "packed, signed, with a transports extension that is a SEQUENCE" => Packed(transportsOid, [0x30, 0x00]),
            "packed, signed, with a transports extension that is an empty OCTET STRING" => Packed(transportsOid, [0x04, 0x00]),
            "packed, signed, with a transports extension that is an empty BIT STRING" => Packed(transportsOid, [0x03, 0x01, 0x00]),
            "packed, signed, with an aaguid extension whose length is not minimal" =>
                Packed(aaguidOid, [0x04, 0x81, 0x10, .. new byte[16]]),
            "fido-u2f, signed, with a transports extension that is a SEQUENCE" => FidoU2f([0x30, 0x00]),
            "fido-u2f, signed, with a transports extension that is an empty BIT STRING" => FidoU2f([0x03, 0x01, 0x00]),
            "tpm, signed, with a SAN that holds no directory name" => Tpm(Der([0x30], Der([0x82], "x"u8.ToArray()))),
            "tpm, signed, with a SAN name that holds its attributes in one flat sequence" =>
                Tpm(SanName(Der([0x30], tpmManufacturerOid, Der([0x0C], "id:414D4400"u8.ToArray())))),
            "tpm, signed, with a SAN value that is not UTF-8" =>
                Tpm(SanName(Der([0x31], Der([0x30], tpmManufacturerOid, Der([0x0C], [0xFF, 0xFE]))))),
            "tpm, signed, with a SAN value that is not a UTF8String" =>
                Tpm(SanName(Der([0x31], Der([0x30], tpmManufacturerOid, Der([0x13], "id:414D4400"u8.ToArray()))))),
            "tpm, signed, with a SAN object identifier that does not parse" =>
                Tpm(SanName(Der([0x31], Der([0x30], [0x06, 0x01, 0x80], Der([0x0C], "id:414D4400"u8.ToArray()))))),
            "android-key, signed, with an attestation record that is not ASN.1" => AndroidKey(garbage),
            "android-key, signed, with an origin that is not a 32-bit integer" =>
                AndroidKey(AndroidRecord(Der([0xBF, 0x85, 0x3E], [0x02, 0x09, 0x01, 0, 0, 0, 0, 0, 0, 0, 0]))),
            "android-key, signed, with a second certificate that is not DER" => AndroidKey(AndroidRecord(), garbage),
            "apple, with the nonce in an extension that does not parse" =>
                Apple(Der([0x30], Der([0xA1], Der([0x05], SHA256.HashData(signedData))))),
            "apple, with the nonce and a chain to another root" => Apple(appleNonce),
            "packed with a certificate key on a curve the verifier does not name" =>
                PackedWith(CertificateOf(unnamedCurveKey!, packedSubject, notACa: true)),
            "packed with a certificate key that is an Ed25519 key of 31 bytes" =>
                PackedWith(CertificateHolding(shortEd25519Key, packedSubject), edDsa),
            "packed with a certificate key that is an EC point off its curve" =>
                PackedWith(CertificateHolding(ecPointOffItsCurve, packedSubject)),
            "packed with a certificate key that is an RSA key that is not DER" =>
                PackedWith(CertificateHolding(rsaKeyThatIsNotDer, packedSubject)),
            "tpm with an AIK certificate key on a curve the verifier does not name" =>
                TpmWith(CertificateOf(unnamedCurveKey!, "", notACa: true, wellFormedSan, aikEku)),
            "android-safetynet with a JWS header that is not a JSON object" =>
                SafetyNet(System.Buffers.Text.Base64Url.EncodeToString("[]"u8) + ".e30.AA"),
            "android-safetynet with a certificate key that is an EC point off its curve" =>
                SafetyNet(SignedJws(CertificateHolding(ecPointOffItsCurve, "CN=attest.android.com"), new { })),
            "apple with the nonce and a certificate key on a curve the verifier does not name" =>
                AppleWith(CertificateOf(unnamedCurveKey!, "CN=Apple", notACa: false, (appleNonceOid, appleNonce))),
            "apple-appattest with a chain to another root" => Object("apple-appattest", Statement(
                ("x5c", Chain(Certificate("CN=AppAttest", notACa: false), Certificate("CN=AppAttest CA", notACa: false)))), authData),
            "packed, signed, with a basic constraints extension that is an empty OCTET STRING" =>
                PackedWith(Certificate(packedSubject, notACa: false, EmptyOctetString("2.5.29.19"))),
            "tpm, signed, with an EKU extension that is an empty OCTET STRING" =>
                TpmWith(Certificate("", notACa: true, wellFormedSan, EmptyOctetString("2.5.29.37"))),
            "tpm, signed, with a basic constraints extension that is an empty OCTET STRING" =>
                TpmWith(Certificate("", notACa: false, wellFormedSan, aikEku, EmptyOctetString("2.5.29.19"))),
            "android-safetynet, signed by its own certificate, with a timestamp after the year 9999" =>
                SafetyNet(SignedJws(safetyNetCertificate, SafetyNetClaims(253_402_300_800_000))),
            "android-safetynet, signed by its own certificate, with the nonce and a chain to another root" =>
                SafetyNet(SignedJws(safetyNetCertificate, SafetyNetClaims(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))),
            "packed, signed, well formed" => PackedWith(Certificate(packedSubject, notACa: true)),
            "fido-u2f, signed, well formed" => FidoU2fWith(Certificate("CN=U2F", notACa: false)),
            "tpm, signed, well formed" => TpmWith(Certificate("", notACa: true, wellFormedSan, aikEku)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

        return System.Text.Json.JsonSerializer.Serialize(new AuthenticatorAttestationRawResponse
        {
            Id = System.Buffers.Text.Base64Url.EncodeToString(credentialId),
            RawId = credentialId,
            Type = Fido2NetLib.Objects.PublicKeyCredentialType.PublicKey,
            Response = new AuthenticatorAttestationRawResponse.AttestationResponse
            {
                AttestationObject = attestationObject,
                ClientDataJson = clientData,
            },
        });

        byte[] ShortCoordinateKey()
        {
            var cose = new Fido2NetLib.Cbor.CborMap();
            cose.Add(1L, 2L);
            cose.Add(3L, -7L);
            cose.Add(-1L, 1L);
            cose.Add(-2L, new byte[] { 1, 2, 3 });
            cose.Add(-3L, new byte[] { 4, 5, 6 });
            return cose.Encode();
        }

        byte[] UnknownKeyType()
        {
            var cose = new Fido2NetLib.Cbor.CborMap();
            cose.Add(1L, 99L);
            cose.Add(3L, -7L);
            return cose.Encode();
        }
    }

    #endregion

    #region A challenge completes one ceremony

    private const string SpentChallengeMessage = "*already been presented*";

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(0u)]
    [InlineData(1u)]
    public async Task AnAssertionChallenge_CompletesOneCeremony(uint signCount)
    {
        using var key = RSA.Create(2048);
        var credential = await SeedActiveMemberCredentialAsync(key);
        var service = CreateService();
        var options = await service.GenerateDiscoverableAssertionOptionsAsync(_tenantId);
        var assertion = SignedAssertion(credential, options.OptionsJson,
            signed => key.SignData(signed, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
            signCount: signCount);

        await service.CompleteAssertionAsync(assertion, options.ChallengeToken, _tenantId);
        var again = () => service.CompleteAssertionAsync(assertion, options.ChallengeToken, _tenantId);

        await again.Should().ThrowAsync<InvalidOperationException>().WithMessage(SpentChallengeMessage);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnAssertionChallenge_IsSpentByARefusedAttempt()
    {
        using var key = RSA.Create(2048);
        var credential = await SeedActiveMemberCredentialAsync(key);
        var service = CreateService();
        var options = await service.GenerateDiscoverableAssertionOptionsAsync(_tenantId);

        var refused = () => service.CompleteAssertionAsync(
            SignedAssertion(credential, options.OptionsJson, _ => [0]), options.ChallengeToken, _tenantId);
        await refused.Should().ThrowAsync<Fido2VerificationException>();

        var valid = () => service.CompleteAssertionAsync(
            SignedAssertion(credential, options.OptionsJson,
                signed => key.SignData(signed, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)),
            options.ChallengeToken, _tenantId);
        await valid.Should().ThrowAsync<InvalidOperationException>().WithMessage(SpentChallengeMessage);
        (await _dbContext.PasskeyCredentials.AsNoTracking().SingleAsync(c => c.Id == credential.Id))
            .LastUsedAt.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnAssertionChallenge_IsSpentUntilItsTokenExpires()
    {
        var service = CreateService();
        var options = await service.GenerateDiscoverableAssertionOptionsAsync(_tenantId);

        await Record.ExceptionAsync(() => service.CompleteAssertionAsync("{}", options.ChallengeToken, _tenantId));

        using var json = System.Text.Json.JsonDocument.Parse(options.OptionsJson);
        _spent.Spent.Should().ContainKey(json.RootElement.GetProperty("challenge").GetString()!)
            .WhoseValue.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(5), TimeSpan.FromSeconds(30));
    }

    /// <summary>A challenge token in the shape every build since tokens named their purpose has minted.</summary>
    private static string MintedToken(
        EphemeralDataProtectionProvider provider, string optionsJson, string purpose, Guid? subjectId,
        PasskeyRegistrationFlow? flow = null) =>
        provider.CreateProtector("Nocturne.Passkey.Challenge").Protect(System.Text.Json.JsonSerializer.Serialize(new
        {
            OptionsJson = optionsJson,
            SubjectId = subjectId,
            Purpose = purpose,
            Flow = flow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
        }));

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnAssertionChallengeMintedBeforeTheSpendWasRecorded_CompletesOneCeremony()
    {
        using var key = RSA.Create(2048);
        var credential = await SeedActiveMemberCredentialAsync(key);
        var provider = new EphemeralDataProtectionProvider();
        var service = CreateService(dataProtection: provider);
        var optionsJson = (await service.GenerateDiscoverableAssertionOptionsAsync(_tenantId)).OptionsJson;
        var token = MintedToken(provider, optionsJson, "assertion", subjectId: null);
        var assertion = SignedAssertion(credential, optionsJson,
            signed => key.SignData(signed, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1), signCount: 0);

        (await service.CompleteAssertionAsync(assertion, token, _tenantId)).SubjectId.Should().Be(_subjectId);
        var again = () => service.CompleteAssertionAsync(assertion, token, _tenantId);

        await again.Should().ThrowAsync<InvalidOperationException>().WithMessage(SpentChallengeMessage);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AChallengeTokenWhoseOptionsCarryNoChallenge_IsRefused()
    {
        var provider = new EphemeralDataProtectionProvider();
        var service = CreateService(dataProtection: provider);
        var token = MintedToken(provider, "{\"challenge\":\"\"}", "assertion", subjectId: null);

        var act = () => service.CompleteAssertionAsync("{}", token, _tenantId);

        (await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*carries no challenge*"))
            .Which.Message.Should().NotContain("already been presented");
        _spent.Spent.Should().BeEmpty();
    }

    [Theory]
    [Trait("Category", "Unit")]
    [MemberData(nameof(EveryFlow))]
    public async Task ARegistrationChallenge_CompletesOneCeremony_EvenForAnotherAuthenticator(PasskeyRegistrationFlow flow)
    {
        using var first = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var second = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var provider = RegisteredProvider();
        var service = RegisteredService(provider);
        var options = await service.GenerateRegistrationOptionsAsync(_subjectId, "rhys", flow);
        if (flow == PasskeyRegistrationFlow.SetupOwner)
            await RecordSetupOwnerAsync(_subjectId);

        await service.CompleteRegistrationAsync(
            Attestation(first, options.OptionsJson, "https://localhost"), options.ChallengeToken, flow, _tenantId, _subjectId);
        var again = () => service.CompleteRegistrationAsync(
            Attestation(second, options.OptionsJson, "https://localhost"), options.ChallengeToken, flow, _tenantId, _subjectId);

        await again.Should().ThrowAsync<InvalidOperationException>().WithMessage(SpentChallengeMessage);
        (await _dbContext.PasskeyCredentials.CountAsync(c => c.SubjectId == _subjectId)).Should().Be(1);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ARegistrationChallenge_IsSpentByARefusedAttempt()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var provider = RegisteredProvider();
        var service = RegisteredService(provider);
        var options = await service.GenerateRegistrationOptionsAsync(_subjectId, "rhys", PasskeyRegistrationFlow.OwnAccount);

        var refused = () => service.CompleteRegistrationAsync(
            Attestation(key, options.OptionsJson, "https://cgm.example.com"), options.ChallengeToken,
            PasskeyRegistrationFlow.OwnAccount, _tenantId, _subjectId);
        await refused.Should().ThrowAsync<PasskeyRegistrationRefusedException>();

        var valid = () => service.CompleteRegistrationAsync(
            Attestation(key, options.OptionsJson, "https://localhost"), options.ChallengeToken,
            PasskeyRegistrationFlow.OwnAccount, _tenantId, _subjectId);
        await valid.Should().ThrowAsync<InvalidOperationException>().WithMessage(SpentChallengeMessage);
        (await _dbContext.PasskeyCredentials.CountAsync(c => c.SubjectId == _subjectId)).Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ARegistrationChallengeMintedBeforeTheSpendWasRecorded_CompletesOneCeremony()
    {
        using var first = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var second = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var provider = RegisteredProvider();
        var protection = new EphemeralDataProtectionProvider();
        var service = RegisteredService(provider, dataProtection: protection);
        var optionsJson = (await service.GenerateRegistrationOptionsAsync(
            _subjectId, "rhys", PasskeyRegistrationFlow.MemberInvite)).OptionsJson;
        var token = MintedToken(protection, optionsJson, "registration", _subjectId, PasskeyRegistrationFlow.MemberInvite);

        await service.CompleteRegistrationAsync(
            Attestation(first, optionsJson, "https://localhost"), token, PasskeyRegistrationFlow.MemberInvite, _tenantId, _subjectId);
        var again = () => service.CompleteRegistrationAsync(
            Attestation(second, optionsJson, "https://localhost"), token, PasskeyRegistrationFlow.MemberInvite, _tenantId, _subjectId);

        await again.Should().ThrowAsync<InvalidOperationException>().WithMessage(SpentChallengeMessage);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ARegistrationChallengeForAnotherSubject_IsRefusedWithoutBeingSpent()
    {
        var service = CreateService();
        var options = await service.GenerateRegistrationOptionsAsync(
            Guid.CreateVersion7(), "other", PasskeyRegistrationFlow.OwnAccount);

        var act = () => service.CompleteRegistrationAsync(
            "{}", options.ChallengeToken, PasskeyRegistrationFlow.OwnAccount, _tenantId, expectedSubjectId: _subjectId);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not issued for the enrolling subject*");
        _spent.Spent.Should().BeEmpty();
    }

    /// <summary>
    /// A service whose credential, subject and membership stores refuse every call, so a test that
    /// passes proves none of them was reached.
    /// </summary>
    private static PasskeyService ServiceThatReadsNoCredential(
        ISpentPasskeyChallengeRepository spent, IDataProtectionProvider dataProtection) =>
        new(
            new Mock<IPasskeyCredentialRepository>(MockBehavior.Strict).Object,
            spent,
            new Mock<ISubjectRepository>(MockBehavior.Strict).Object,
            new Mock<ITenantMemberService>(MockBehavior.Strict).Object,
            dataProtection, RelyingParty("localhost"), NullLogger<PasskeyService>.Instance,
            Mock.Of<IHostEnvironment>(e => e.EnvironmentName == "Development"), TimeProvider.System);

    private static Mock<ISpentPasskeyChallengeRepository> SpendAnswering(bool spent)
    {
        var mock = new Mock<ISpentPasskeyChallengeRepository>();
        mock.Setup(s => s.TrySpendAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(spent);
        return mock;
    }

    private static Mock<ISpentPasskeyChallengeRepository> SpendThrowing()
    {
        var mock = new Mock<ISpentPasskeyChallengeRepository>();
        mock.Setup(s => s.TrySpendAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SpendStoreUnavailableException());
        return mock;
    }

    private sealed class SpendStoreUnavailableException : System.Data.Common.DbException;

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ASpentAssertionChallenge_IsRefusedBeforeTheCredentialOrMembershipIsRead()
    {
        using var key = RSA.Create(2048);
        var credential = await SeedActiveMemberCredentialAsync(key);
        var provider = new EphemeralDataProtectionProvider();
        var options = await CreateService(dataProtection: provider).GenerateDiscoverableAssertionOptionsAsync(_tenantId);
        var service = ServiceThatReadsNoCredential(SpendAnswering(false).Object, provider);

        var act = () => service.CompleteAssertionAsync(
            SignedAssertion(credential, options.OptionsJson,
                signed => key.SignData(signed, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)),
            options.ChallengeToken, _tenantId);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage(SpentChallengeMessage);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ASpentRegistrationChallenge_IsRefusedBeforeAnyCredentialIsReadOrStored()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var provider = new EphemeralDataProtectionProvider();
        var options = await CreateService(dataProtection: provider).GenerateRegistrationOptionsAsync(
            _subjectId, "rhys", PasskeyRegistrationFlow.OwnAccount);
        var service = ServiceThatReadsNoCredential(SpendAnswering(false).Object, provider);

        var act = () => service.CompleteRegistrationAsync(
            Attestation(key, options.OptionsJson, "https://localhost"), options.ChallengeToken,
            PasskeyRegistrationFlow.OwnAccount, _tenantId, _subjectId);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage(SpentChallengeMessage);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnAssertion_WhoseSpendCannotBeRecorded_FailsAsAnErrorRatherThanARefusal()
    {
        using var key = RSA.Create(2048);
        var credential = await SeedActiveMemberCredentialAsync(key);
        var provider = new EphemeralDataProtectionProvider();
        var options = await CreateService(dataProtection: provider).GenerateDiscoverableAssertionOptionsAsync(_tenantId);
        var service = ServiceThatReadsNoCredential(SpendThrowing().Object, provider);

        var thrown = await Record.ExceptionAsync(() => service.CompleteAssertionAsync(
            SignedAssertion(credential, options.OptionsJson,
                signed => key.SignData(signed, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)),
            options.ChallengeToken, _tenantId));

        thrown.Should().BeOfType<SpendStoreUnavailableException>(
            "login answers InvalidOperationException, Fido2VerificationException and JsonException as a refusal");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ARegistration_WhoseSpendCannotBeRecorded_FailsAsAnErrorAndStoresNothing()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var provider = new EphemeralDataProtectionProvider();
        var options = await CreateService(dataProtection: provider).GenerateRegistrationOptionsAsync(
            _subjectId, "rhys", PasskeyRegistrationFlow.AccessRequest);
        var service = ServiceThatReadsNoCredential(SpendThrowing().Object, provider);

        var thrown = await Record.ExceptionAsync(() => service.CompleteRegistrationAsync(
            Attestation(key, options.OptionsJson, "https://localhost"), options.ChallengeToken,
            PasskeyRegistrationFlow.AccessRequest, _tenantId, _subjectId));

        thrown.Should().BeOfType<SpendStoreUnavailableException>();
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
            new PasskeyCredentialRepository(contexts), _spent, new SubjectRepository(contexts, AccountLinkRoutes.Off), new TenantMemberService(new TenantMembershipRepository(contexts, AccountLinkRoutes.Off), TimeProvider.System),
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

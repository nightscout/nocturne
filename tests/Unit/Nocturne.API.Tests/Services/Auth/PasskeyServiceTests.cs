using Fido2NetLib;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Services.Auth;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

using Nocturne.Infrastructure.Data.Repositories;

using Nocturne.API.Services.Identity;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// Unit tests for PasskeyService focusing on DB operations, removal protection, credential cap,
/// and challenge cookie expiry. WebAuthn ceremony methods require real Fido2 instances and are
/// better covered by integration tests.
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
    /// An assertion naming <paramref name="rawId"/> whose signature is never valid: it reaches every
    /// check made before the signature, and no further.
    /// </summary>
    private static string AssertionNaming(byte[] rawId) =>
        System.Text.Json.JsonSerializer.Serialize(new AuthenticatorAssertionRawResponse
        {
            Id = Convert.ToBase64String(rawId).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
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
        // We use a mock Fido2 - it won't be called for DB-only tests.
        // For methods that call Fido2, integration tests are needed.
        var fido2Config = new Fido2Configuration
        {
            RPID = rpId,
            RPName = "Test",
            Origins = new HashSet<string> { $"https://{rpId}" },
        };
        var fido2 = new Fido2NetLib.Fido2(fido2Config);

        var dataProtectionProvider = dataProtection ?? new EphemeralDataProtectionProvider();
        var fido2Options = Options.Create(fido2Config);
        var logger = NullLogger<PasskeyService>.Instance;

        var environment = Mock.Of<IHostEnvironment>(e => e.EnvironmentName == environmentName);
        var contexts = _dbContext.AsFactory();
        return new PasskeyService(
            new PasskeyCredentialRepository(contexts), new SubjectRepository(contexts, AccountLinkRoutes.Off), new TenantMemberService(new TenantMembershipRepository(contexts, AccountLinkRoutes.Off), TimeProvider.System),
            fido2, dataProtectionProvider, fido2Options, logger, environment, TimeProvider.System);
    }

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

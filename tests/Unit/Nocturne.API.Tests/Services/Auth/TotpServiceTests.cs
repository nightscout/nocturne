using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.Auth;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// Unit tests for TotpService covering setup, verification, and credential management.
/// </summary>
/// <remarks>
/// Backed by SQLite rather than the InMemory provider: consuming a TOTP time step uses a
/// conditional <c>ExecuteUpdate</c>, which only relational providers support.
/// </remarks>
public class TotpServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly NocturneDbContext _dbContext;
    private readonly IDataProtectionProvider _dataProtectionProvider;
    private readonly Guid _subjectId = Guid.CreateVersion7();
    private const string TestUsername = "testuser";

    public TotpServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        _dbContext = new NocturneDbContext(options);
        _dbContext.Database.EnsureCreated();
        _dataProtectionProvider = new EphemeralDataProtectionProvider();
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
    }

    #region GenerateSetupAsync

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GenerateSetupAsync_ReturnsProvisioningUri()
    {
        var service = CreateService();

        var result = await service.GenerateSetupAsync(_subjectId, TestUsername);

        result.ProvisioningUri.Should().StartWith("otpauth://totp/Nocturne:");
        result.ProvisioningUri.Should().Contain(TestUsername);
        result.ProvisioningUri.Should().Contain("secret=");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GenerateSetupAsync_ReturnsBase32Secret()
    {
        var service = CreateService();

        var result = await service.GenerateSetupAsync(_subjectId, TestUsername);

        result.Base32Secret.Should().NotBeNullOrWhiteSpace();
        // Base32 chars only
        result.Base32Secret.Should().MatchRegex("^[A-Z2-7]+$");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GenerateSetupAsync_ReturnsChallengeToken()
    {
        var service = CreateService();

        var result = await service.GenerateSetupAsync(_subjectId, TestUsername);

        result.ChallengeToken.Should().NotBeNullOrWhiteSpace();
    }

    #endregion

    #region CompleteSetupAsync

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CompleteSetupAsync_WithValidCode_PersistsCredential()
    {
        var service = CreateService();
        SeedSubject(TestUsername, _subjectId);
        var setup = await service.GenerateSetupAsync(_subjectId, TestUsername);

        // Generate a valid TOTP code from the secret
        var secret = TotpHelper.GenerateSecret();
        // We need to use the actual secret from the setup, so we'll compute the code
        // from the base32 secret. Instead, use the challenge token flow end-to-end.
        var code = GenerateValidCode(setup.Base32Secret);

        var result = await service.CompleteSetupAsync(code, "My Authenticator", setup.ChallengeToken);

        result.SubjectId.Should().Be(_subjectId);
        result.CredentialId.Should().NotBeEmpty();

        var entity = await _dbContext.TotpCredentials.FirstOrDefaultAsync(c => c.Id == result.CredentialId);
        entity.Should().NotBeNull();
        entity!.SubjectId.Should().Be(_subjectId);
        entity.Label.Should().Be("My Authenticator");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CompleteSetupAsync_WithInvalidCode_Throws()
    {
        var service = CreateService();
        var setup = await service.GenerateSetupAsync(_subjectId, TestUsername);

        var act = () => service.CompleteSetupAsync("000000", "Label", setup.ChallengeToken);

        (await act.Should().ThrowAsync<TotpSetupException>())
            .Which.Failure.Should().Be(TotpSetupFailure.InvalidCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CompleteSetupAsync_WithTamperedToken_Throws()
    {
        var service = CreateService();

        var act = () => service.CompleteSetupAsync("123456", "Label", "tampered-token");

        (await act.Should().ThrowAsync<TotpSetupException>())
            .Which.Failure.Should().Be(TotpSetupFailure.ChallengeUnreadable);
    }

    /// <summary>
    /// A challenge that is readable but stale is refused as expired, not as unreadable: the two
    /// are the same 400 to the caller but different copy, so the split has to survive here.
    /// </summary>
    /// <remarks>
    /// The hand-minted payload is only evidence if it deserializes — a shape
    /// <see cref="System.Text.Json.JsonSerializer"/> could not read would leave
    /// <c>ExpiresAt</c> at <c>default</c>, which is also in the past. The accepted case proves the
    /// property names match, so the refused one can only be the expiry.
    /// </remarks>
    [Theory]
    [InlineData(-1, true)]
    [InlineData(1, false)]
    [Trait("Category", "Unit")]
    public async Task CompleteSetupAsync_RefusesOnlyTheChallengeThatHasExpired(
        int minutesFromNow, bool expectRefusal)
    {
        var service = CreateService();
        SeedSubject(TestUsername, _subjectId);
        var secret = TotpHelper.GenerateSecret();

        var setupProtector = _dataProtectionProvider.CreateProtector("Nocturne.Totp.Setup");
        var challenge = setupProtector.Protect(JsonSerializer.Serialize(new
        {
            Secret = secret,
            SubjectId = _subjectId,
            ExpiresAt = DateTime.UtcNow.AddMinutes(minutesFromNow),
        }));

        // A code the verifier accepts, so the expiry is the only thing that can refuse.
        var code = TotpHelper.ComputeTotp(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var act = () => service.CompleteSetupAsync(code, "Label", challenge);

        if (expectRefusal)
        {
            (await act.Should().ThrowAsync<TotpSetupException>())
                .Which.Failure.Should().Be(TotpSetupFailure.ChallengeExpired);
        }
        else
        {
            (await act()).SubjectId.Should().Be(_subjectId);
        }
    }

    #endregion

    #region VerifyStepUpAsync

    [Fact]
    [Trait("Category", "Unit")]
    public async Task VerifyStepUpAsync_WithValidCredential_ReturnsSubject()
    {
        var service = CreateService();

        // Seed subject and credential
        var secret = TotpHelper.GenerateSecret();
        var subject = SeedSubject(TestUsername);
        SeedCredential(subject.Id, secret, "Test TOTP");

        var code = TotpHelper.ComputeTotp(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        var result = await service.VerifyStepUpAsync(await service.CreateStepUpTokenAsync(subject.Id), code);

        result.Should().NotBeNull();
        result!.SubjectId.Should().Be(subject.Id);
        result.Username.Should().Be(TestUsername);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task VerifyStepUpAsync_WithoutAStepUpToken_ReturnsNull()
    {
        var service = CreateService();

        var secret = TotpHelper.GenerateSecret();
        var subject = SeedSubject(TestUsername);
        SeedCredential(subject.Id, secret, "Test TOTP");

        var code = TotpHelper.ComputeTotp(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        // A correct code is worthless without proof that a primary factor completed.
        var result = await service.VerifyStepUpAsync("not-a-real-token", code);

        result.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task VerifyStepUpAsync_WithATokenForAnotherSubject_ReturnsNull()
    {
        var service = CreateService();

        var secret = TotpHelper.GenerateSecret();
        var subject = SeedSubject(TestUsername);
        SeedCredential(subject.Id, secret, "Test TOTP");
        var otherSubject = SeedSubject("othersubject");

        var code = TotpHelper.ComputeTotp(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        var result = await service.VerifyStepUpAsync(
            await service.CreateStepUpTokenAsync(otherSubject.Id), code);

        result.Should().BeNull("the step-up token names the account, so a code cannot be replayed onto another");
    }

    /// <summary>
    /// A step-up token is a reference to a persisted row keyed on the subject, so one cannot be
    /// minted for an account that does not exist.
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task CreateStepUpTokenAsync_ForAnUnknownSubject_Throws()
    {
        var service = CreateService();

        var act = () => service.CreateStepUpTokenAsync(Guid.CreateVersion7());

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task VerifyStepUpAsync_WithADeactivatedSubject_ReturnsNull()
    {
        var service = CreateService();

        var secret = TotpHelper.GenerateSecret();
        var subject = SeedSubject(TestUsername);
        SeedCredential(subject.Id, secret, "Test TOTP");

        var stepUpToken = await service.CreateStepUpTokenAsync(subject.Id);
        await _dbContext.Subjects.Where(s => s.Id == subject.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));

        var code = TotpHelper.ComputeTotp(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var result = await service.VerifyStepUpAsync(stepUpToken, code);

        result.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task VerifyStepUpAsync_WithWrongCode_ReturnsNull()
    {
        var service = CreateService();

        var secret = TotpHelper.GenerateSecret();
        var subject = SeedSubject(TestUsername);
        SeedCredential(subject.Id, secret, "Test TOTP");

        var result = await service.VerifyStepUpAsync(await service.CreateStepUpTokenAsync(subject.Id), "000000");

        result.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task VerifyStepUpAsync_RecordsTheConsumedStepAndLastUsedAt()
    {
        var service = CreateService();

        var secret = TotpHelper.GenerateSecret();
        var subject = SeedSubject(TestUsername);
        var credential = SeedCredential(subject.Id, secret, "Test TOTP");

        credential.LastUsedAt.Should().BeNull();
        credential.LastUsedStep.Should().BeNull();

        var code = TotpHelper.ComputeTotp(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await service.VerifyStepUpAsync(await service.CreateStepUpTokenAsync(subject.Id), code);

        var updated = await _dbContext.TotpCredentials.AsNoTracking().FirstAsync(c => c.Id == credential.Id);
        updated.LastUsedAt.Should().NotBeNull();
        updated.LastUsedStep.Should().NotBeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task VerifyStepUpAsync_RejectsTheSameCodeASecondTimeWithinItsWindow()
    {
        var service = CreateService();

        var secret = TotpHelper.GenerateSecret();
        var subject = SeedSubject(TestUsername);
        var credential = SeedCredential(subject.Id, secret, "Test TOTP");

        var code = TotpHelper.ComputeTotp(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        var first = await service.VerifyStepUpAsync(await service.CreateStepUpTokenAsync(subject.Id), code);
        first.Should().NotBeNull();

        var consumedStep = (await _dbContext.TotpCredentials.AsNoTracking()
            .FirstAsync(c => c.Id == credential.Id)).LastUsedStep;
        consumedStep.Should().NotBeNull();

        // The code still matches an accepted time step, and it is the recorded one — so single use
        // is the only thing left that can refuse it, not a crossed step boundary.
        TotpHelper.TryVerify(secret, code, lastUsedStep: null, out var stillMatchedStep).Should().BeTrue();
        stillMatchedStep.Should().Be(consumedStep!.Value);

        // Same code, still inside the ±1 step acceptance window, fresh step-up token.
        var freshToken = await service.CreateStepUpTokenAsync(subject.Id);
        var second = await service.VerifyStepUpAsync(freshToken, code);

        second.Should().BeNull("a code is consumed on first use, not valid for the whole window");

        var freshRow = await _dbContext.TotpStepUpTokens.AsNoTracking()
            .Where(t => t.SubjectId == subject.Id)
            .OrderByDescending(t => t.Id)
            .FirstAsync();
        freshRow.ConsumedAt.Should().BeNull("the reused code is refused before the token is spent");
    }

    /// <summary>
    /// A step-up token stands for one completed primary factor, so it must buy one session. Without
    /// this, a captured token was worth a session for every valid code presented inside its
    /// five-minute window — including the next window's code, which is not the one already consumed.
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task VerifyStepUpAsync_RejectsAStepUpTokenRedeemedASecondTime()
    {
        var service = CreateService();

        var secret = TotpHelper.GenerateSecret();
        var subject = SeedSubject(TestUsername);
        var credential = SeedCredential(subject.Id, secret, "Test TOTP");

        var stepUpToken = await service.CreateStepUpTokenAsync(subject.Id);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var first = await service.VerifyStepUpAsync(
            stepUpToken, TotpHelper.ComputeTotp(secret, now));
        first.Should().NotBeNull();

        var consumedStep = (await _dbContext.TotpCredentials.AsNoTracking()
            .FirstAsync(c => c.Id == credential.Id)).LastUsedStep;
        (await _dbContext.TotpStepUpTokens.AsNoTracking().SingleAsync(t => t.SubjectId == subject.Id))
            .ConsumedAt.Should().NotBeNull();

        // A different, still-valid code — asserted valid against the consumed step, so the reuse is
        // caught by the token rather than by a crossed step boundary.
        var nextCode = TotpHelper.ComputeTotp(secret, now + 30);
        TotpHelper.TryVerify(secret, nextCode, consumedStep, out _).Should()
            .BeTrue("the second attempt has to fail on the token, not on the code");

        var second = await service.VerifyStepUpAsync(stepUpToken, nextCode);

        second.Should().BeNull("a step-up token proves one primary factor, so it yields one session");
        (await _dbContext.TotpCredentials.AsNoTracking().FirstAsync(c => c.Id == credential.Id))
            .LastUsedStep.Should().Be(consumedStep, "a refused redemption consumes nothing further");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task VerifyStepUpAsync_MarksTheStepUpTokenConsumed()
    {
        var service = CreateService();

        var secret = TotpHelper.GenerateSecret();
        var subject = SeedSubject(TestUsername);
        SeedCredential(subject.Id, secret, "Test TOTP");

        var code = TotpHelper.ComputeTotp(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await service.VerifyStepUpAsync(await service.CreateStepUpTokenAsync(subject.Id), code);

        var row = await _dbContext.TotpStepUpTokens.AsNoTracking()
            .SingleAsync(t => t.SubjectId == subject.Id);
        row.ConsumedAt.Should().NotBeNull();
    }

    /// <summary>
    /// A wrong code must not burn the token, or a typo would send the user back through the passkey
    /// assertion.
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task VerifyStepUpAsync_WrongCode_LeavesTheStepUpTokenRedeemable()
    {
        var service = CreateService();

        var secret = TotpHelper.GenerateSecret();
        var subject = SeedSubject(TestUsername);
        SeedCredential(subject.Id, secret, "Test TOTP");

        var stepUpToken = await service.CreateStepUpTokenAsync(subject.Id);

        (await service.VerifyStepUpAsync(stepUpToken, "000000")).Should().BeNull();

        var code = TotpHelper.ComputeTotp(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var retried = await service.VerifyStepUpAsync(stepUpToken, code);

        retried.Should().NotBeNull();
    }

    /// <summary>
    /// The token carries a reference to server state, not the subject, so a token whose row is gone
    /// (expired and pruned) cannot still name an account.
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task VerifyStepUpAsync_WithNoMatchingStepUpRow_ReturnsNull()
    {
        var service = CreateService();

        var secret = TotpHelper.GenerateSecret();
        var subject = SeedSubject(TestUsername);
        SeedCredential(subject.Id, secret, "Test TOTP");

        var stepUpToken = await service.CreateStepUpTokenAsync(subject.Id);
        await _dbContext.TotpStepUpTokens.Where(t => t.SubjectId == subject.Id).ExecuteDeleteAsync();

        var code = TotpHelper.ComputeTotp(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var result = await service.VerifyStepUpAsync(stepUpToken, code);

        result.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task VerifyStepUpAsync_WithAnExpiredStepUpRow_ReturnsNull()
    {
        var service = CreateService();

        var secret = TotpHelper.GenerateSecret();
        var subject = SeedSubject(TestUsername);
        SeedCredential(subject.Id, secret, "Test TOTP");

        var stepUpToken = await service.CreateStepUpTokenAsync(subject.Id);
        await _dbContext.TotpStepUpTokens
            .Where(t => t.SubjectId == subject.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));

        var code = TotpHelper.ComputeTotp(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var result = await service.VerifyStepUpAsync(stepUpToken, code);

        result.Should().BeNull("the row is the authority on expiry, not the token payload");
    }

    /// <summary>
    /// The setup protector and the step-up protector have distinct Data Protection purposes, so a
    /// token minted under the setup purpose is not step-up proof. Everything else about this token
    /// is genuine — it names a real unconsumed step-up row and is presented with a valid code — so
    /// the purpose split is the only thing refusing it: unify the purposes and the redemption
    /// succeeds. Setup is a flow where the caller already knows the secret, which is why a token
    /// from it must never assert that a primary factor completed.
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public async Task VerifyStepUpAsync_WithATokenMintedUnderTheSetupPurpose_ReturnsNull()
    {
        var service = CreateService();

        var secret = TotpHelper.GenerateSecret();
        var subject = SeedSubject(TestUsername);
        SeedCredential(subject.Id, secret, "Test TOTP");

        await service.CreateStepUpTokenAsync(subject.Id);
        var row = await _dbContext.TotpStepUpTokens.AsNoTracking()
            .SingleAsync(t => t.SubjectId == subject.Id);

        var setupProtector = _dataProtectionProvider.CreateProtector("Nocturne.Totp.Setup");
        var mintedUnderSetup = setupProtector.Protect(JsonSerializer.Serialize(new
        {
            TokenId = row.Id,
            row.ExpiresAt,
        }));

        var code = TotpHelper.ComputeTotp(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var result = await service.VerifyStepUpAsync(mintedUnderSetup, code);

        result.Should().BeNull();
        (await _dbContext.TotpStepUpTokens.AsNoTracking().SingleAsync(t => t.Id == row.Id))
            .ConsumedAt.Should().BeNull("a token the step-up protector cannot read never reaches the row");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CompleteSetupAsync_ConsumesTheCodeThatProvedSetup()
    {
        var service = CreateService();
        SeedSubject(TestUsername, _subjectId);
        var setup = await service.GenerateSetupAsync(_subjectId, TestUsername);
        var code = GenerateValidCode(setup.Base32Secret);

        var created = await service.CompleteSetupAsync(code, "My Authenticator", setup.ChallengeToken);

        var result = await service.VerifyStepUpAsync(await service.CreateStepUpTokenAsync(_subjectId), code);

        result.Should().BeNull("the setup code is recorded as consumed, so it cannot also sign in");
        var entity = await _dbContext.TotpCredentials.AsNoTracking().FirstAsync(c => c.Id == created.CredentialId);
        entity.LastUsedStep.Should().NotBeNull();
    }

    #endregion

    #region GetCredentialsAsync

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetCredentialsAsync_ReturnsRegisteredCredentials()
    {
        var service = CreateService();

        SeedCredential(_subjectId, TotpHelper.GenerateSecret(), "App 1");
        SeedCredential(_subjectId, TotpHelper.GenerateSecret(), "App 2");
        // Another subject's credential - should not be returned
        SeedCredential(Guid.CreateVersion7(), TotpHelper.GenerateSecret(), "Other");

        var credentials = await service.GetCredentialsAsync(_subjectId);

        credentials.Should().HaveCount(2);
        credentials.Select(c => c.Label).Should().BeEquivalentTo(["App 1", "App 2"]);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetCredentialsAsync_DoesNotExposeSecret()
    {
        var service = CreateService();
        SeedCredential(_subjectId, TotpHelper.GenerateSecret(), "My App");

        var credentials = await service.GetCredentialsAsync(_subjectId);

        // TotpCredentialInfo record has no Secret property
        credentials.Should().HaveCount(1);
        var info = credentials[0];
        info.Id.Should().NotBeEmpty();
        info.Label.Should().Be("My App");
    }

    #endregion

    #region RemoveCredentialAsync

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RemoveCredentialAsync_DeletesFromDb()
    {
        var service = CreateService();
        var credential = SeedCredential(_subjectId, TotpHelper.GenerateSecret(), "To Delete");

        await service.RemoveCredentialAsync(credential.Id, _subjectId);

        var exists = await _dbContext.TotpCredentials.AnyAsync(c => c.Id == credential.Id);
        exists.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RemoveCredentialAsync_WrongSubject_Throws()
    {
        var service = CreateService();
        var credential = SeedCredential(_subjectId, TotpHelper.GenerateSecret(), "Credential");

        var act = () => service.RemoveCredentialAsync(credential.Id, Guid.CreateVersion7());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RemoveCredentialAsync_NonexistentId_Throws()
    {
        var service = CreateService();

        var act = () => service.RemoveCredentialAsync(Guid.CreateVersion7(), _subjectId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    #endregion

    #region GetCredentialCountAsync

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetCredentialCountAsync_ReturnsCorrectCount()
    {
        var service = CreateService();

        SeedCredential(_subjectId, TotpHelper.GenerateSecret(), "A");
        SeedCredential(_subjectId, TotpHelper.GenerateSecret(), "B");

        var count = await service.GetCredentialCountAsync(_subjectId);

        count.Should().Be(2);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetCredentialCountAsync_NoCredentials_ReturnsZero()
    {
        var service = CreateService();

        var count = await service.GetCredentialCountAsync(_subjectId);

        count.Should().Be(0);
    }

    #endregion

    #region Helpers

    private TotpService CreateService()
    {
        return new TotpService(_dbContext, _dataProtectionProvider, NullLogger<TotpService>.Instance);
    }

    private SubjectEntity SeedSubject(string username, Guid? subjectId = null)
    {
        var subject = new SubjectEntity
        {
            Id = subjectId ?? Guid.CreateVersion7(),
            Name = username,
            Username = username,
            IsActive = true,
        };

        _dbContext.Subjects.Add(subject);
        _dbContext.SaveChanges();
        return subject;
    }

    private TotpCredentialEntity SeedCredential(Guid subjectId, byte[] secret, string label)
    {
        // SQLite enforces the subject foreign key, so the owning subject has to exist.
        if (!_dbContext.Subjects.Any(s => s.Id == subjectId))
        {
            SeedSubject($"user-{subjectId:N}", subjectId);
        }

        var entity = new TotpCredentialEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = subjectId,
            SecretKey = secret,
            Label = label,
            CreatedAt = DateTime.UtcNow,
        };

        _dbContext.TotpCredentials.Add(entity);
        _dbContext.SaveChanges();
        return entity;
    }

    /// <summary>
    /// Decodes a base32 secret and computes a valid TOTP code for the current time.
    /// </summary>
    private static string GenerateValidCode(string base32Secret)
    {
        var secret = FromBase32(base32Secret);
        return TotpHelper.ComputeTotp(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    /// <summary>
    /// Decodes RFC 4648 base32 (no padding) back to bytes.
    /// </summary>
    private static byte[] FromBase32(string base32)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new List<byte>();
        var buffer = 0;
        var bitsLeft = 0;

        foreach (var c in base32.ToUpperInvariant())
        {
            var val = alphabet.IndexOf(c);
            if (val < 0) continue;

            buffer = (buffer << 5) | val;
            bitsLeft += 5;

            if (bitsLeft >= 8)
            {
                bitsLeft -= 8;
                output.Add((byte)(buffer >> bitsLeft));
            }
        }

        return output.ToArray();
    }

    #endregion
}

using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.Auth;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// Unit tests for RecoveryCodeService
/// </summary>
public class RecoveryCodeServiceTests : IDisposable
{
    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _dbContext;
    private readonly RecoveryCodeService _service;
    private readonly Guid _subjectId = Guid.CreateVersion7();

    public RecoveryCodeServiceTests()
    {
        _db = TestDbContextFactory.CreateSqlite();
        _dbContext = _db.CreateContext();

        _dbContext.Subjects.Add(new SubjectEntity
        {
            Id = _subjectId,
            Name = "recovery-code-subject",
        });
        _dbContext.SaveChanges();

        _service = new RecoveryCodeService(_dbContext, CheapDerive);
    }

    /// <summary>
    /// Stands in for the 100,000-iteration PBKDF2 in every test that is not about the derivation
    /// itself: salted and one-way like the real one, at a microsecond instead of tens of
    /// milliseconds per code. <see cref="ProductionDerivation_IsPbkdf2Sha256AndRoundTrips"/> covers
    /// the real one.
    /// </summary>
    private static byte[] CheapDerive(string normalizedCode, byte[] salt) =>
        SHA256.HashData([.. salt, .. Encoding.UTF8.GetBytes(normalizedCode)]);

    public void Dispose()
    {
        _dbContext.Dispose();
        _db.Dispose();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GenerateCodesAsync_Returns8Codes()
    {
        var codes = await _service.GenerateCodesAsync(_subjectId);

        codes.Should().HaveCount(8);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GenerateCodesAsync_CodesMatchExpectedFormat()
    {
        var codes = await _service.GenerateCodesAsync(_subjectId);

        foreach (var code in codes)
        {
            code.Should().MatchRegex(@"^[A-Z2-9]{5}-[A-Z2-9]{5}$");
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GenerateCodesAsync_CodesStoredAsHashesInDb()
    {
        var codes = await _service.GenerateCodesAsync(_subjectId);

        var entities = await _dbContext.RecoveryCodes
            .Where(r => r.SubjectId == _subjectId)
            .ToListAsync();

        entities.Should().HaveCount(8);

        var plainCodes = codes.Select(c => c.Replace("-", "")).ToHashSet();
        foreach (var entity in entities)
        {
            plainCodes.Should().NotContain(entity.CodeHash);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GenerateCodesAsync_StoresSaltedPbkdf2Hashes()
    {
        await _service.GenerateCodesAsync(_subjectId);

        var hashes = await _dbContext.RecoveryCodes
            .Where(r => r.SubjectId == _subjectId)
            .Select(r => r.CodeHash)
            .ToListAsync();

        foreach (var hash in hashes)
        {
            hash.Should().MatchRegex(@"^pbkdf2-sha256\$100000\$[A-Za-z0-9+/=]+\$[A-Za-z0-9+/=]+$");
        }

        var salts = hashes.Select(h => h.Split('$')[2]).ToList();
        salts.Should().OnlyHaveUniqueItems("each code gets its own salt");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GenerateCodesAsync_RegenerationInvalidatesExistingCodes()
    {
        await _service.GenerateCodesAsync(_subjectId);
        await _service.GenerateCodesAsync(_subjectId);

        var entities = await _dbContext.RecoveryCodes
            .Where(r => r.SubjectId == _subjectId)
            .ToListAsync();

        entities.Should().HaveCount(8, "regeneration should delete old codes and create exactly 8 new ones");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task VerifyAndConsumeAsync_ValidCode_ReturnsTrue()
    {
        var codes = await _service.GenerateCodesAsync(_subjectId);

        var result = await _service.VerifyAndConsumeAsync(_subjectId, codes[0]);

        result.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task VerifyAndConsumeAsync_CaseInsensitive()
    {
        var codes = await _service.GenerateCodesAsync(_subjectId);
        var lowerCode = codes[0].ToLowerInvariant();

        var result = await _service.VerifyAndConsumeAsync(_subjectId, lowerCode);

        result.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task VerifyAndConsumeAsync_UsedCode_ReturnsFalse()
    {
        var codes = await _service.GenerateCodesAsync(_subjectId);

        await _service.VerifyAndConsumeAsync(_subjectId, codes[0]);
        var result = await _service.VerifyAndConsumeAsync(_subjectId, codes[0]);

        result.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task VerifyAndConsumeAsync_InvalidCode_ReturnsFalse()
    {
        await _service.GenerateCodesAsync(_subjectId);

        var result = await _service.VerifyAndConsumeAsync(_subjectId, "AAAAA-BBBBB");

        result.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task VerifyAndConsumeAsync_UnknownSubject_ReturnsFalse()
    {
        await _service.GenerateCodesAsync(_subjectId);

        var result = await _service.VerifyAndConsumeAsync(null, "AAAAA-BBBBB");

        result.Should().BeFalse();
    }

    private (RecoveryCodeService Service, Func<int> Derivations) CountingService()
    {
        var count = 0;
        var service = new RecoveryCodeService(_dbContext, (code, salt) =>
        {
            count++;
            return CheapDerive(code, salt);
        });
        return (service, () => count);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("unknown user")]
    [InlineData("no live codes")]
    [InlineData("eight live codes, wrong code")]
    [InlineData("eight live codes, matching code")]
    public async Task VerifyAndConsumeAsync_DerivesAgainstEightSlotsWhateverTheSubject(string scenario)
    {
        var (service, derivations) = CountingService();
        Guid? subject = scenario == "unknown user" ? null : _subjectId;
        var code = "AAAAA-AAAAA";

        if (scenario.StartsWith("eight"))
        {
            var codes = await service.GenerateCodesAsync(_subjectId);
            if (scenario.EndsWith("matching code"))
                code = codes[3];
        }

        var before = derivations();
        await service.VerifyAndConsumeAsync(subject, code);

        (derivations() - before).Should().Be(8);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ProductionDerivation_IsPbkdf2Sha256AndRoundTrips()
    {
        var salt = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        RecoveryCodeService.Derive("AAAAABBBBB", salt).Should().Equal(
            Rfc2898DeriveBytes.Pbkdf2("AAAAABBBBB", salt, 100_000, HashAlgorithmName.SHA256, 32));

        var service = new RecoveryCodeService(_dbContext);
        var codes = await service.GenerateCodesAsync(_subjectId);

        (await service.VerifyAndConsumeAsync(_subjectId, codes[0].ToLowerInvariant())).Should().BeTrue();
        (await service.VerifyAndConsumeAsync(_subjectId, codes[0])).Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task VerifyAndConsumeAsync_RowAtAnotherIterationCount_NeverVerifies()
    {
        var codes = await _service.GenerateCodesAsync(_subjectId);
        var entity = await _dbContext.RecoveryCodes.FirstAsync(r => r.SubjectId == _subjectId);
        entity.CodeHash = entity.CodeHash.Replace("$100000$", "$1$");
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        foreach (var code in codes)
            await _service.VerifyAndConsumeAsync(_subjectId, code);

        (await _service.GetRemainingCountAsync(_subjectId)).Should().Be(1);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task VerifyAndConsumeAsync_InvalidatedCode_ReturnsFalse()
    {
        var codes = await _service.GenerateCodesAsync(_subjectId);

        await _dbContext.RecoveryCodes
            .Where(r => r.SubjectId == _subjectId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.InvalidatedAt, DateTime.UtcNow));

        var result = await _service.VerifyAndConsumeAsync(_subjectId, codes[0]);

        result.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetRemainingCountAsync_ReturnsCorrectCountAfterConsumption()
    {
        var codes = await _service.GenerateCodesAsync(_subjectId);

        var initialCount = await _service.GetRemainingCountAsync(_subjectId);
        initialCount.Should().Be(8);

        await _service.VerifyAndConsumeAsync(_subjectId, codes[0]);
        await _service.VerifyAndConsumeAsync(_subjectId, codes[1]);

        var remaining = await _service.GetRemainingCountAsync(_subjectId);
        remaining.Should().Be(6);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetRemainingCountAsync_ExcludesInvalidated()
    {
        await _service.GenerateCodesAsync(_subjectId);

        await _dbContext.RecoveryCodes
            .Where(r => r.SubjectId == _subjectId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.InvalidatedAt, DateTime.UtcNow));

        var remaining = await _service.GetRemainingCountAsync(_subjectId);

        remaining.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HasCodesAsync_WithCodes_ReturnsTrue()
    {
        await _service.GenerateCodesAsync(_subjectId);

        var result = await _service.HasCodesAsync(_subjectId);

        result.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HasCodesAsync_WithoutCodes_ReturnsFalse()
    {
        var result = await _service.HasCodesAsync(_subjectId);

        result.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HasCodesAsync_WithOnlyInvalidated_ReturnsFalse()
    {
        await _service.GenerateCodesAsync(_subjectId);

        await _dbContext.RecoveryCodes
            .Where(r => r.SubjectId == _subjectId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.InvalidatedAt, DateTime.UtcNow));

        var result = await _service.HasCodesAsync(_subjectId);

        result.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task WereCodesResetAsync_WithOnlyInvalidated_ReturnsTrue()
    {
        await _service.GenerateCodesAsync(_subjectId);

        await _dbContext.RecoveryCodes
            .Where(r => r.SubjectId == _subjectId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.InvalidatedAt, DateTime.UtcNow));

        var result = await _service.WereCodesResetAsync(_subjectId);

        result.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task WereCodesResetAsync_AfterGeneratingCodes_ReturnsFalse()
    {
        await _service.GenerateCodesAsync(_subjectId);

        var result = await _service.WereCodesResetAsync(_subjectId);

        result.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task WereCodesResetAsync_WithNoCodes_ReturnsFalse()
    {
        var result = await _service.WereCodesResetAsync(_subjectId);

        result.Should().BeFalse();
    }
}

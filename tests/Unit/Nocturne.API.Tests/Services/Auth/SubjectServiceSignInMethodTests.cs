using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Auth;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// Exercises <see cref="SubjectService.HasSingleSignInMethodAsync"/> — the predicate behind the
/// "add a backup way to sign in" prompt, widening
/// <see cref="SubjectService.CountPrimaryAuthFactorsAsync"/> with the recovery-code leg — against
/// a real EF InMemory DbContext.
/// </summary>
public class SubjectServiceSignInMethodTests : IDisposable
{
    private readonly NocturneDbContext _db;
    private readonly SubjectService _service;
    private readonly Mock<IRecoveryCodeService> _recoveryCodes = new();
    private readonly Guid _subjectId = Guid.CreateVersion7();

    public SubjectServiceSignInMethodTests()
    {
        _db = TestDbContextFactory.CreateInMemoryContext();
        _service = new SubjectService(
            _db,
            Mock.Of<IAuthAuditService>(),
            _recoveryCodes.Object,
            NullLogger<SubjectService>.Instance
        );
    }

    public void Dispose() => _db.Dispose();

    private async Task SeedPasskeysAsync(int count)
    {
        for (var i = 0; i < count; i++)
        {
            _db.PasskeyCredentials.Add(new PasskeyCredentialEntity
            {
                Id = Guid.CreateVersion7(),
                SubjectId = _subjectId,
                CredentialId = [(byte)i],
            });
        }
        await _db.SaveChangesAsync();
    }

    private async Task SeedOidcIdentityAsync()
    {
        _db.SubjectOidcIdentities.Add(new SubjectOidcIdentityEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = _subjectId,
            ProviderId = Guid.CreateVersion7(),
            OidcSubjectId = "ext-sub",
            Issuer = "https://issuer.example",
            LinkedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
    }

    private void GiveRecoveryCodes(int unused) =>
        _recoveryCodes.Setup(s => s.GetRemainingCountAsync(_subjectId)).ReturnsAsync(unused);

    [Fact]
    [Trait("Category", "Unit")]
    public async Task OnePasskeyAndNoRecoveryCodes_IsASingleMethod()
    {
        await SeedPasskeysAsync(1);
        GiveRecoveryCodes(0);

        (await _service.HasSingleSignInMethodAsync(_subjectId)).Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task OnePasskeyWithUnusedRecoveryCodes_IsNotASingleMethod()
    {
        await SeedPasskeysAsync(1);
        GiveRecoveryCodes(8);

        (await _service.HasSingleSignInMethodAsync(_subjectId)).Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TwoPasskeys_IsNotASingleMethod()
    {
        await SeedPasskeysAsync(2);
        GiveRecoveryCodes(0);

        (await _service.HasSingleSignInMethodAsync(_subjectId)).Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task OnePasskeyAndOneLinkedIdentity_IsNotASingleMethod()
    {
        await SeedPasskeysAsync(1);
        await SeedOidcIdentityAsync();
        GiveRecoveryCodes(0);

        (await _service.HasSingleSignInMethodAsync(_subjectId)).Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task OneLinkedIdentityAndNoRecoveryCodes_IsASingleMethod()
    {
        await SeedOidcIdentityAsync();
        GiveRecoveryCodes(0);

        (await _service.HasSingleSignInMethodAsync(_subjectId)).Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task NoPrimaryFactors_IsNotASingleMethod()
    {
        GiveRecoveryCodes(0);

        (await _service.HasSingleSignInMethodAsync(_subjectId)).Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AnotherSubjectsPasskey_DoesNotCount()
    {
        _db.PasskeyCredentials.Add(new PasskeyCredentialEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = Guid.CreateVersion7(),
            CredentialId = [1],
        });
        await _db.SaveChangesAsync();
        await SeedPasskeysAsync(1);
        GiveRecoveryCodes(0);

        (await _service.HasSingleSignInMethodAsync(_subjectId)).Should().BeTrue();
    }
}

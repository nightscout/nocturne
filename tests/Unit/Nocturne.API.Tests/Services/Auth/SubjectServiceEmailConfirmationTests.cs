using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// When a provider's assertion confirms an account's address, and when it must not: only a
/// verified email, only the address the account holds, never by changing it on a link.
/// </summary>
public class SubjectServiceEmailConfirmationTests : IDisposable
{
    private static readonly DateTime Earlier = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private const string Issuer = "https://issuer.example";

    private readonly SqliteTestDatabase _db = TestDbContextFactory.CreateSqlite();
    private readonly SubjectService _service;

    public SubjectServiceEmailConfirmationTests()
    {
        _service = new SubjectService(
            new SubjectRepository(_db.ContextFactory.AsNocturneFactory(), AccountLinkRoutes.Off),
            Mock.Of<IAuthAuditService>(),
            Mock.Of<IRecoveryCodeService>(),
            new NoProviderGrants(),
            NullLogger<SubjectService>.Instance,
            TimeProvider.System);
    }

    public void Dispose() => _db.Dispose();

    private async Task<Guid> SeedSubjectAsync(string? email = null, DateTime? confirmedAt = null)
    {
        var entity = new SubjectEntity { Id = Guid.CreateVersion7(), Name = "alice", IsActive = true };
        entity.SetEmail(email);
        if (email is not null && confirmedAt is { } at)
            entity.ConfirmEmail(email, at);
        await using var db = _db.CreateContext();
        db.Subjects.Add(entity);
        await db.SaveChangesAsync();
        return entity.Id;
    }

    private async Task<Guid> SeedProviderAsync()
    {
        var id = Guid.CreateVersion7();
        await using var db = _db.CreateContext();
        db.OidcProviders.Add(new OidcProviderEntity
        {
            Id = id, Name = "Keycloak", IssuerUrl = Issuer, ClientId = "nocturne", IsEnabled = true,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> SeedLinkedSubjectAsync(Guid providerId, string? email, DateTime? confirmedAt = null)
    {
        var subjectId = await SeedSubjectAsync(email, confirmedAt);
        await using var db = _db.CreateContext();
        db.SubjectOidcIdentities.Add(new SubjectOidcIdentityEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = subjectId,
            ProviderId = providerId,
            OidcSubjectId = "ext-1",
            Issuer = Issuer,
            LinkedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return subjectId;
    }

    private async Task<SubjectEntity> RowAsync(Guid id)
    {
        await using var db = _db.CreateContext();
        return await db.Subjects.AsNoTracking().SingleAsync(s => s.Id == id);
    }

    [Fact]
    public async Task SignIn_CreatingAnAccount_WithAVerifiedEmail_StoresItConfirmed()
    {
        var providerId = await SeedProviderAsync();

        var subject = await _service.FindOrCreateFromOidcAsync(
            providerId, "new-ext", Issuer, "new@example.test", emailVerified: true, "New User");

        var row = await RowAsync(subject.Id);
        row.Email.Should().Be("new@example.test");
        row.EmailConfirmedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task SignIn_CreatingAnAccount_WithAnUnverifiedEmail_ConfirmsNothing()
    {
        var providerId = await SeedProviderAsync();

        var subject = await _service.FindOrCreateFromOidcAsync(
            providerId, "new-ext", Issuer, "new@example.test", emailVerified: false, "New User");

        var row = await RowAsync(subject.Id);
        row.Email.Should().BeNull();
        row.EmailConfirmedAt.Should().BeNull();
    }

    [Fact]
    public async Task SignIn_ToAnAccount_WithAVerifiedChangedEmail_AdoptsAndConfirmsIt()
    {
        var providerId = await SeedProviderAsync();
        var subjectId = await SeedLinkedSubjectAsync(providerId, "old@example.test", Earlier);

        await _service.FindOrCreateFromOidcAsync(providerId, "ext-1", Issuer, "new@example.test", emailVerified: true);

        var row = await RowAsync(subjectId);
        row.Email.Should().Be("new@example.test");
        row.EmailConfirmedAt.Should().NotBeNull().And.NotBe(Earlier);
    }

    [Fact]
    public async Task SignIn_ToAnAccount_WithAVerifiedUnchangedEmail_ConfirmsAnUnconfirmedAddress()
    {
        var providerId = await SeedProviderAsync();
        var subjectId = await SeedLinkedSubjectAsync(providerId, "same@example.test");

        await _service.FindOrCreateFromOidcAsync(providerId, "ext-1", Issuer, "same@example.test", emailVerified: true);

        (await RowAsync(subjectId)).EmailConfirmedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task SignIn_ToAnAccount_WithAVerifiedUnchangedEmail_KeepsTheFirstConfirmation()
    {
        var providerId = await SeedProviderAsync();
        var subjectId = await SeedLinkedSubjectAsync(providerId, "same@example.test", Earlier);

        await _service.FindOrCreateFromOidcAsync(providerId, "ext-1", Issuer, "same@example.test", emailVerified: true);

        (await RowAsync(subjectId)).EmailConfirmedAt.Should().Be(Earlier);
    }

    [Fact]
    public async Task SignIn_ToAnAccount_WithAnUnverifiedEmail_ConfirmsNothingAndKeepsTheAddress()
    {
        var providerId = await SeedProviderAsync();
        var subjectId = await SeedLinkedSubjectAsync(providerId, "mine@example.test");

        await _service.FindOrCreateFromOidcAsync(providerId, "ext-1", Issuer, "mine@example.test", emailVerified: false);

        var row = await RowAsync(subjectId);
        row.Email.Should().Be("mine@example.test");
        row.EmailConfirmedAt.Should().BeNull();
    }

    [Fact]
    public async Task SignIn_ToAConfirmedAccount_WithAnUnverifiedClaim_StaysConfirmed()
    {
        var providerId = await SeedProviderAsync();
        var subjectId = await SeedLinkedSubjectAsync(providerId, "mine@example.test", Earlier);

        await _service.FindOrCreateFromOidcAsync(providerId, "ext-1", Issuer, "other@example.test", emailVerified: false);

        var row = await RowAsync(subjectId);
        row.Email.Should().Be("mine@example.test");
        row.EmailConfirmedAt.Should().Be(Earlier);
    }

    [Fact]
    public async Task Link_WithAVerifiedEmailEqualToTheAccountAddress_ConfirmsIt()
    {
        var subjectId = await SeedSubjectAsync("mine@example.test");
        var providerId = await SeedProviderAsync();

        await _service.AttachOidcIdentityAsync(subjectId, providerId, "ext-1", Issuer, "Mine@example.test", emailVerified: true);

        (await RowAsync(subjectId)).EmailConfirmedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Link_WithAVerifiedEmailThatDiffers_LeavesTheAccountUntouched()
    {
        var subjectId = await SeedSubjectAsync("mine@example.test");
        var providerId = await SeedProviderAsync();

        await _service.AttachOidcIdentityAsync(subjectId, providerId, "ext-1", Issuer, "other@example.test", emailVerified: true);

        var row = await RowAsync(subjectId);
        row.Email.Should().Be("mine@example.test");
        row.EmailConfirmedAt.Should().BeNull();
    }

    [Fact]
    public async Task Link_WithAnUnverifiedEmailEqualToTheAccountAddress_ConfirmsNothing()
    {
        var subjectId = await SeedSubjectAsync("mine@example.test");
        var providerId = await SeedProviderAsync();

        await _service.AttachOidcIdentityAsync(subjectId, providerId, "ext-1", Issuer, "mine@example.test", emailVerified: false);

        (await RowAsync(subjectId)).EmailConfirmedAt.Should().BeNull();
    }

    [Fact]
    public async Task Link_ToAnAccountWithNoAddress_NeverAdoptsTheProviderAddress()
    {
        var subjectId = await SeedSubjectAsync();
        var providerId = await SeedProviderAsync();

        await _service.AttachOidcIdentityAsync(subjectId, providerId, "ext-1", Issuer, "other@example.test", emailVerified: true);

        var row = await RowAsync(subjectId);
        row.Email.Should().BeNull();
        row.EmailConfirmedAt.Should().BeNull();
    }

    [Fact]
    public async Task Link_OfAnIdentityLinkedToAnotherAccount_ConfirmsNothingForTheCaller()
    {
        var providerId = await SeedProviderAsync();
        await SeedLinkedSubjectAsync(providerId, email: null);
        var caller = await SeedSubjectAsync("mine@example.test");

        var (outcome, _) = await _service.AttachOidcIdentityAsync(
            caller, providerId, "ext-1", Issuer, "mine@example.test", emailVerified: true);

        outcome.Should().Be(OidcLinkOutcome.AlreadyLinkedToOther);
        (await RowAsync(caller)).EmailConfirmedAt.Should().BeNull();
    }

    [Fact]
    public async Task CreateSubjectAsync_StoresTheAddressUnconfirmed()
    {
        var created = await _service.CreateSubjectAsync(new Nocturne.Core.Models.Authorization.Subject { Name = "Owner", Email = "owner@example.test" });

        var row = await RowAsync(created.Subject!.Id);
        row.Email.Should().Be("owner@example.test");
        row.EmailConfirmedAt.Should().BeNull();
    }

    [Fact]
    public async Task SignIn_ToAnAccount_WithAVerifiedEmailAndANewName_StoresBoth()
    {
        var providerId = await SeedProviderAsync();
        var subjectId = await SeedLinkedSubjectAsync(providerId, email: null);

        await _service.FindOrCreateFromOidcAsync(
            providerId, "ext-1", Issuer, "new@example.test", emailVerified: true, "New Name");

        var row = await RowAsync(subjectId);
        row.Name.Should().Be("New Name");
        row.Email.Should().Be("new@example.test");
        row.EmailConfirmedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task SignIn_ToAConfirmedAccount_WithTheSameAddressInAnotherCase_KeepsTheStoredAddressAndTime()
    {
        var providerId = await SeedProviderAsync();
        var subjectId = await SeedLinkedSubjectAsync(providerId, "mine@example.test", Earlier);

        await _service.FindOrCreateFromOidcAsync(providerId, "ext-1", Issuer, "Mine@Example.test", emailVerified: true);

        var row = await RowAsync(subjectId);
        row.Email.Should().Be("mine@example.test");
        row.EmailConfirmedAt.Should().Be(Earlier);
    }

    [Fact]
    public async Task SignIn_ToAnAccount_WithAnUnverifiedClaimAndANewName_RenamesWithoutTouchingTheAddress()
    {
        var providerId = await SeedProviderAsync();
        var subjectId = await SeedLinkedSubjectAsync(providerId, "mine@example.test", Earlier);

        await _service.FindOrCreateFromOidcAsync(
            providerId, "ext-1", Issuer, "other@example.test", emailVerified: false, "New Name");

        var row = await RowAsync(subjectId);
        row.Name.Should().Be("New Name");
        row.Email.Should().Be("mine@example.test");
        row.EmailConfirmedAt.Should().Be(Earlier);
    }

    [Theory]
    [InlineData("mine@example.test")]
    [InlineData("  MINE@Example.test ")]
    public async Task SignIn_ToAConfirmedAccount_WithTheSameAddress_MakesNoWriteForTheAddress(string asserted)
    {
        var subjectId = Guid.CreateVersion7();
        var identity = new SubjectOidcIdentity { Id = Guid.CreateVersion7(), SubjectId = subjectId, OidcSubjectId = "ext-1", Issuer = Issuer };
        var record = new SubjectRecord(
            subjectId, "alice", null, "Mine@example.test", Earlier, null, true, false, false, false,
            "Approved", null, null, null, null, Earlier, Earlier, null, []);
        var repository = new Mock<ISubjectRepository>();
        repository
            .Setup(r => r.FindByOidcIdentityAsync("ext-1", Issuer, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OidcIdentityMatch(identity, record));
        var service = new SubjectService(
            repository.Object, Mock.Of<IAuthAuditService>(), Mock.Of<IRecoveryCodeService>(),
            new NoProviderGrants(),
            NullLogger<SubjectService>.Instance,
            TimeProvider.System);

        await service.FindOrCreateFromOidcAsync(Guid.NewGuid(), "ext-1", Issuer, asserted, emailVerified: true);

        repository.Verify(
            r => r.ConfirmEmailAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Never);
        repository.Verify(
            r => r.UpdateAsync(It.IsAny<Guid>(), It.IsAny<SubjectEdit>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}

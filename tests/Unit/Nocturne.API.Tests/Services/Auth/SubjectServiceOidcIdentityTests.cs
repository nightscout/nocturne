using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Auth;

/// <summary>
/// Exercises the OIDC identity management methods on <see cref="SubjectService"/>
/// against a real EF InMemory DbContext.
/// </summary>
public class SubjectServiceOidcIdentityTests : IDisposable
{
    private readonly NocturneDbContext _db;
    private readonly SubjectService _service;
    private readonly Mock<IAuthAuditService> _audit = new();

    public SubjectServiceOidcIdentityTests()
    {
        _db = TestDbContextFactory.CreateInMemoryContext();
        _service = new SubjectService(
            _db,
            _audit.Object,
            Mock.Of<IRecoveryCodeService>(),
            NullLogger<SubjectService>.Instance
        );
    }

    public void Dispose() => _db.Dispose();

    private async Task<Guid> SeedSubjectAsync(string name = "alice")
    {
        var id = Guid.CreateVersion7();
        _db.Subjects.Add(new SubjectEntity
        {
            Id = id,
            Name = name,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> SeedProviderAsync(string name = "Keycloak")
    {
        var id = Guid.CreateVersion7();
        _db.OidcProviders.Add(new OidcProviderEntity
        {
            Id = id,
            Name = name,
            IssuerUrl = "https://issuer.example",
            ClientId = "nocturne",
            IsEnabled = true,
        });
        await _db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> SeedIdentityAsync(
        Guid subjectId, Guid providerId, string extSub, string issuer = "https://issuer.example",
        string? email = null)
    {
        var id = Guid.CreateVersion7();
        _db.SubjectOidcIdentities.Add(new SubjectOidcIdentityEntity
        {
            Id = id,
            SubjectId = subjectId,
            ProviderId = providerId,
            OidcSubjectId = extSub,
            Issuer = issuer,
            Email = email,
            LinkedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
        return id;
    }

    // -- AttachOidcIdentityAsync -------------------------------------------------

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AttachOidcIdentityAsync_WhenNotLinked_CreatesRowAndReturnsCreated()
    {
        var subjectId = await SeedSubjectAsync();
        var providerId = await SeedProviderAsync();

        var (outcome, identityId) = await _service.AttachOidcIdentityAsync(
            subjectId, providerId, "ext-sub-1", "https://issuer.example", "a@b.com");

        outcome.Should().Be(OidcLinkOutcome.Created);
        identityId.Should().NotBeNull();

        var row = await _db.SubjectOidcIdentities.AsNoTracking()
            .SingleAsync(x => x.Id == identityId!.Value);
        row.SubjectId.Should().Be(subjectId);
        row.ProviderId.Should().Be(providerId);
        row.OidcSubjectId.Should().Be("ext-sub-1");
        row.Issuer.Should().Be("https://issuer.example");
        row.Email.Should().Be("a@b.com");
        row.LinkedAt.Should().NotBe(default);
        row.LastUsedAt.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AttachOidcIdentityAsync_WhenAlreadyLinkedToSameSubject_ReturnsIdempotent()
    {
        var subjectId = await SeedSubjectAsync();
        var providerId = await SeedProviderAsync();
        var existingId = await SeedIdentityAsync(subjectId, providerId, "ext-sub-1", email: "old@x");

        var (outcome, identityId) = await _service.AttachOidcIdentityAsync(
            subjectId, providerId, "ext-sub-1", "https://issuer.example", "new@x");

        outcome.Should().Be(OidcLinkOutcome.AlreadyLinkedToSelf);
        identityId.Should().Be(existingId);

        var row = await _db.SubjectOidcIdentities.AsNoTracking().SingleAsync(x => x.Id == existingId);
        row.LastUsedAt.Should().NotBeNull();
        row.Email.Should().Be("new@x");

        var count = await _db.SubjectOidcIdentities.CountAsync();
        count.Should().Be(1);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AttachOidcIdentityAsync_WhenLinkedToDifferentSubject_ReturnsAlreadyLinkedToOther()
    {
        var subjectA = await SeedSubjectAsync("alice");
        var subjectB = await SeedSubjectAsync("bob");
        var providerId = await SeedProviderAsync();
        await SeedIdentityAsync(subjectA, providerId, "ext-sub-1");

        var (outcome, identityId) = await _service.AttachOidcIdentityAsync(
            subjectB, providerId, "ext-sub-1", "https://issuer.example", "x@y");

        outcome.Should().Be(OidcLinkOutcome.AlreadyLinkedToOther);
        identityId.Should().BeNull();

        var rows = await _db.SubjectOidcIdentities.AsNoTracking().ToListAsync();
        rows.Should().HaveCount(1);
        rows[0].SubjectId.Should().Be(subjectA);
    }

    // -- TryRemoveOidcIdentityAsync ----------------------------------------------

    private async Task SeedPasskeyAsync(Guid subjectId, byte tag = 0)
    {
        _db.PasskeyCredentials.Add(new PasskeyCredentialEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = subjectId,
            CredentialId = new byte[] { tag, 1, 2, 3 },
            PublicKey = new byte[] { 4, 5, 6 },
            SignCount = 0,
            Label = $"pk{tag}",
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TryRemoveOidcIdentityAsync_WhenNotOwned_ReturnsNotFound()
    {
        var a = await SeedSubjectAsync("a");
        var b = await SeedSubjectAsync("b");
        var providerId = await SeedProviderAsync();
        var idA = await SeedIdentityAsync(a, providerId, "ext-a");
        await SeedIdentityAsync(b, providerId, "ext-b");
        // Keep b with another primary factor so the last-factor guard isn't what's blocking.
        await SeedPasskeyAsync(b);

        var result = await _service.TryRemoveOidcIdentityAsync(b, idA);

        result.Should().Be(FactorRemovalResult.NotFound);
        (await _db.SubjectOidcIdentities.CountAsync()).Should().Be(2);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TryRemoveOidcIdentityAsync_WhenLastPrimaryFactor_ReturnsLastPrimaryFactor_DoesNotDelete()
    {
        var subjectId = await SeedSubjectAsync();
        var providerId = await SeedProviderAsync();
        var idId = await SeedIdentityAsync(subjectId, providerId, "ext-1");

        var result = await _service.TryRemoveOidcIdentityAsync(subjectId, idId);

        result.Should().Be(FactorRemovalResult.LastPrimaryFactor);
        (await _db.SubjectOidcIdentities.CountAsync()).Should().Be(1);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TryRemoveOidcIdentityAsync_WhenMultipleFactors_RemovesAndReturnsRemoved()
    {
        var subjectId = await SeedSubjectAsync();
        var providerId = await SeedProviderAsync();
        var idId = await SeedIdentityAsync(subjectId, providerId, "ext-1");
        await SeedPasskeyAsync(subjectId);

        var result = await _service.TryRemoveOidcIdentityAsync(subjectId, idId);

        result.Should().Be(FactorRemovalResult.Removed);
        (await _db.SubjectOidcIdentities.CountAsync()).Should().Be(0);
        (await _db.PasskeyCredentials.CountAsync()).Should().Be(1);
    }

    // -- CountPrimaryAuthFactorsAsync --------------------------------------------

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CountPrimaryAuthFactorsAsync_ReturnsSumOfPasskeysAndOidcIdentities()
    {
        var subjectId = await SeedSubjectAsync();
        var providerId = await SeedProviderAsync();

        for (var i = 0; i < 2; i++)
        {
            _db.PasskeyCredentials.Add(new PasskeyCredentialEntity
            {
                Id = Guid.CreateVersion7(),
                SubjectId = subjectId,
                CredentialId = new byte[] { (byte)i, 1, 2, 3 },
                PublicKey = new byte[] { 4, 5, 6 },
                SignCount = 0,
                Label = $"pk{i}",
                CreatedAt = DateTime.UtcNow,
            });
        }
        await _db.SaveChangesAsync();

        await SeedIdentityAsync(subjectId, providerId, "e1");
        await SeedIdentityAsync(subjectId, providerId, "e2");
        await SeedIdentityAsync(subjectId, providerId, "e3");

        var count = await _service.CountPrimaryAuthFactorsAsync(subjectId);
        count.Should().Be(5);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CountPrimaryAuthFactorsAsync_DoesNotCountTotps()
    {
        var subjectId = await SeedSubjectAsync();
        _db.TotpCredentials.Add(new TotpCredentialEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = subjectId,
            SecretKey = new byte[] { 1, 2, 3 },
            Label = "authenticator",
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        var count = await _service.CountPrimaryAuthFactorsAsync(subjectId);
        count.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CountPrimaryAuthFactorsAsync_ReturnsZeroWhenNoFactors()
    {
        var subjectId = await SeedSubjectAsync();
        var count = await _service.CountPrimaryAuthFactorsAsync(subjectId);
        count.Should().Be(0);
    }

    // -- GetLinkedOidcIdentitiesAsync --------------------------------------------

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetLinkedOidcIdentitiesAsync_ReturnsOnlyCallerIdentities()
    {
        var a = await SeedSubjectAsync("a");
        var b = await SeedSubjectAsync("b");
        var providerId = await SeedProviderAsync();
        await SeedIdentityAsync(a, providerId, "a-1");
        await SeedIdentityAsync(a, providerId, "a-2");
        await SeedIdentityAsync(b, providerId, "b-1");

        var result = await _service.GetLinkedOidcIdentitiesAsync(a);

        result.Should().HaveCount(2);
        result.Select(i => i.OidcSubjectId).Should().BeEquivalentTo(new[] { "a-1", "a-2" });
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetLinkedOidcIdentitiesAsync_IncludesProviderNameFromNavigation()
    {
        var subjectId = await SeedSubjectAsync();
        var providerId = await SeedProviderAsync("MyIdP");
        await SeedIdentityAsync(subjectId, providerId, "ext-1");

        var result = await _service.GetLinkedOidcIdentitiesAsync(subjectId);

        result.Should().HaveCount(1);
        result[0].ProviderName.Should().Be("MyIdP");
    }

    // -- FindOrCreateFromOidcAsync -----------------------------------------------

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FindOrCreateFromOidcAsync_OnCreate_InsertsSubjectAndIdentityAtomically()
    {
        var providerId = await SeedProviderAsync();

        var subject = await _service.FindOrCreateFromOidcAsync(
            providerId, "new-ext", "https://issuer.example", "new@x", "New User");

        subject.Should().NotBeNull();
        var sRow = await _db.Subjects.AsNoTracking().SingleAsync(s => s.Id == subject.Id);
        sRow.Email.Should().Be("new@x");
        sRow.Name.Should().Be("New User");

        var iRow = await _db.SubjectOidcIdentities.AsNoTracking().SingleAsync();
        iRow.SubjectId.Should().Be(subject.Id);
        iRow.OidcSubjectId.Should().Be("new-ext");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FindOrCreateFromOidcAsync_OnFind_ReturnsExistingSubjectAndUpdatesLastUsedAt()
    {
        var subjectId = await SeedSubjectAsync("alice");
        var providerId = await SeedProviderAsync();
        var identityId = await SeedIdentityAsync(subjectId, providerId, "ext-1");

        var subject = await _service.FindOrCreateFromOidcAsync(
            providerId, "ext-1", "https://issuer.example");

        subject.Id.Should().Be(subjectId);

        var row = await _db.SubjectOidcIdentities.AsNoTracking().SingleAsync(x => x.Id == identityId);
        row.LastUsedAt.Should().NotBeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FindOrCreateFromOidcAsync_OnFind_UpdatesSubjectEmailAndNameIfChanged()
    {
        var subjectId = await SeedSubjectAsync("old-name");
        var providerId = await SeedProviderAsync();
        await SeedIdentityAsync(subjectId, providerId, "ext-1");

        await _service.FindOrCreateFromOidcAsync(
            providerId, "ext-1", "https://issuer.example", "new@e", "New Name");

        var sRow = await _db.Subjects.AsNoTracking().SingleAsync(s => s.Id == subjectId);
        sRow.Email.Should().Be("new@e");
        sRow.Name.Should().Be("New Name");
    }

    // -- UpdateOidcIdentityLastUsedAsync -----------------------------------------

    [Fact]
    [Trait("Category", "Unit")]
    public async Task UpdateOidcIdentityLastUsedAsync_WhenExists_UpdatesTimestamp()
    {
        var subjectId = await SeedSubjectAsync();
        var providerId = await SeedProviderAsync();
        var identityId = await SeedIdentityAsync(subjectId, providerId, "ext-1");

        await _service.UpdateOidcIdentityLastUsedAsync(identityId);

        var row = await _db.SubjectOidcIdentities.AsNoTracking().SingleAsync(x => x.Id == identityId);
        row.LastUsedAt.Should().NotBeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task UpdateOidcIdentityLastUsedAsync_WhenNotExists_NoOp_DoesNotThrow()
    {
        var act = async () => await _service.UpdateOidcIdentityLastUsedAsync(Guid.NewGuid());
        await act.Should().NotThrowAsync();
    }
}

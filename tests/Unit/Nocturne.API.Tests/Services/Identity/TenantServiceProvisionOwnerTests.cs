using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nocturne.API.Configuration;
using Nocturne.API.Services.Auth;
using Nocturne.API.Services.Identity;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Nocturne.Infrastructure.Data.Repositories;
using Xunit;

namespace Nocturne.API.Tests.Services.Identity;

/// <summary>
/// Which subject <see cref="TenantService.ProvisionWithOwnerAsync"/> makes the owner of a new tenant.
/// </summary>
[Trait("Category", "Unit")]
public sealed class TenantServiceProvisionOwnerTests : IDisposable
{
    private sealed class SharedSqliteFactory(DbContextOptions<NocturneDbContext> options)
        : IDbContextFactory<NocturneDbContext>
    {
        public NocturneDbContext CreateDbContext() => new(options);
    }

    private readonly SqliteTestDatabase _db = TestDbContextFactory.CreateSqlite();

    public void Dispose() => _db.Dispose();

    private TenantService Provisioner() => new(
        new SharedSqliteFactory(_db.Options).AsNocturneFactory(),
        new TenantRepository(new SharedSqliteFactory(_db.Options).AsNocturneFactory()),
        new TenantMembershipRepository(new SharedSqliteFactory(_db.Options).AsNocturneFactory()),
        new SubjectRepository(new SharedSqliteFactory(_db.Options).AsNocturneFactory()),
        new MemoryCache(new MemoryCacheOptions()),
        Options.Create(new OperatorConfiguration()),
        Mock.Of<IHttpClientFactory>(),
        Mock.Of<ITenantRoleService>(),
        new GrantRevocationService(
            new GuestSessionCacheService(new MemoryCache(new MemoryCacheOptions()))),
        TimeProvider.System,
        NullLogger<TenantService>.Instance);

    private async Task<Guid> SeedSubjectAsync(string email)
    {
        await using var db = _db.CreateContext();
        var subject = new SubjectEntity
        {
            Id = Guid.CreateVersion7(),
            Name = email,
            Email = email,
            IsActive = true,
            ApprovalStatus = "Approved",
        };
        db.Subjects.Add(subject);
        await db.SaveChangesAsync();
        return subject.Id;
    }

    private static ProvisionCredentialData Passkey(Guid subjectId) => new(
        CredentialId: Convert.ToBase64String([1, 2, 3]),
        PublicKey: Convert.ToBase64String([4, 5, 6]),
        SignCount: 0,
        Transports: [],
        AaGuid: null,
        SubjectId: subjectId);

    [Fact]
    public async Task A_passkey_owner_is_never_resolved_to_an_existing_subject_by_email()
    {
        var existing = await SeedSubjectAsync("owner@example.com");
        var credentialSubject = Guid.CreateVersion7();

        var result = await Provisioner().ProvisionWithOwnerAsync(
            "alpha", "Alpha", "alpha-owner", "owner@example.com",
            Passkey(credentialSubject), oidcIdentity: null);

        result.SubjectId.Should().Be(credentialSubject);

        await using var db = _db.CreateContext();
        (await db.PasskeyCredentials.AnyAsync(p => p.SubjectId == existing))
            .Should().BeFalse("the new owner's passkey belongs to the new owner");
        (await db.TenantMembers.AnyAsync(m => m.SubjectId == existing))
            .Should().BeFalse();
    }

    [Fact]
    public async Task A_credential_naming_an_existing_subject_is_refused_and_creates_nothing()
    {
        var existing = await SeedSubjectAsync("someone@example.com");

        var act = () => Provisioner().ProvisionWithOwnerAsync(
            "alpha", "Alpha", "alpha-owner", "owner@example.com",
            Passkey(existing), oidcIdentity: null);

        await act.Should().ThrowAsync<ProvisionConflictException>();
        await using var db = _db.CreateContext();
        (await db.Tenants.AnyAsync(t => t.Slug == "alpha")).Should().BeFalse();
        (await db.PasskeyCredentials.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task An_already_linked_oidc_identity_names_the_owner_even_with_a_different_email()
    {
        var linked = await SeedSubjectAsync("owner@gmail.com");
        await SeedSubjectAsync("owner@mac.com");
        await using (var db = _db.CreateContext())
        {
            var provider = new OidcProviderEntity
            {
                Id = Guid.CreateVersion7(),
                Name = "Google",
                IssuerUrl = "https://accounts.google.com",
                ClientId = "client",
                IsEnabled = true,
            };
            db.OidcProviders.Add(provider);
            db.SubjectOidcIdentities.Add(new SubjectOidcIdentityEntity
            {
                Id = Guid.CreateVersion7(),
                SubjectId = linked,
                ProviderId = provider.Id,
                OidcSubjectId = "google-sub",
                Issuer = "https://accounts.google.com",
                LinkedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var result = await Provisioner().ProvisionWithOwnerAsync(
            "alpha", "Alpha", "alpha-owner", "owner@mac.com",
            credential: null,
            new ProvisionOidcIdentityData(
                "Google", "google-sub", "https://accounts.google.com/", "owner@gmail.com", SubjectId: null));

        result.SubjectId.Should().Be(linked);
    }
}

using FluentAssertions;
using Nocturne.API.Services.Identity;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.API.Tests.Services.Identity;

/// <summary>
/// Whether the instance is past first-run setup gates anonymous access, so an identity the holder
/// cannot sign in with must not count as a credential here.
/// </summary>
[Trait("Category", "Unit")]
public class InstanceSetupStateTests : IDisposable
{
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly SqliteTestDatabase _db;
    private readonly InstanceSetupState _state;

    public InstanceSetupStateTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(_tenantId, "site");
        _state = new InstanceSetupState(_db.ContextFactory);
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task SeedOnlyMemberWithOidcAsync(bool providerEnabled)
    {
        using var db = _db.CreateContext();

        var providerId = Guid.CreateVersion7();
        db.OidcProviders.Add(new OidcProviderEntity
        {
            Id = providerId,
            Name = "Test provider",
            IssuerUrl = "https://idp.invalid",
            ClientId = "client",
            IsEnabled = providerEnabled,
        });

        var subjectId = Guid.CreateVersion7();
        db.Subjects.Add(new SubjectEntity { Id = subjectId, Name = "alice", IsActive = true });
        db.SubjectOidcIdentities.Add(new SubjectOidcIdentityEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = subjectId,
            ProviderId = providerId,
            Issuer = "https://idp.invalid",
            OidcSubjectId = "ext-1",
        });
        db.TenantMembers.Add(new TenantMemberEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = _tenantId,
            SubjectId = subjectId,
        });

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task An_enabled_provider_identity_completes_setup()
    {
        await SeedOnlyMemberWithOidcAsync(providerEnabled: true);

        (await _state.IsSetupCompleteAsync()).Should().BeTrue();
        (await _state.TenantHasCredentialedMemberAsync(_tenantId)).Should().BeTrue();
    }

    [Fact]
    public async Task An_identity_on_a_disabled_provider_does_not_complete_setup()
    {
        await SeedOnlyMemberWithOidcAsync(providerEnabled: false);

        (await _state.IsSetupCompleteAsync()).Should().BeFalse();
        (await _state.TenantHasCredentialedMemberAsync(_tenantId)).Should().BeFalse();
    }
}

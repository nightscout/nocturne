using Microsoft.EntityFrameworkCore;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Tests.Shared.Infrastructure;

namespace Nocturne.Infrastructure.Data.Tests.Extensions;

/// <summary>
/// Every sign-in check shares this filter, so a provider row it reads wrongly either strands a
/// tenant in recovery mode or lets a locked-out account go unnoticed.
/// </summary>
[Trait("Category", "Unit")]
public class WorkingOidcIdentityFilterTests : IDisposable
{
    private readonly NocturneDbContext _context;

    public WorkingOidcIdentityFilterTests()
    {
        _context = TestDbContextFactory.CreateInMemoryContext();
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<Guid> SeedIdentityAsync(bool providerExists, bool isEnabled)
    {
        var providerId = Guid.CreateVersion7();
        if (providerExists)
        {
            _context.OidcProviders.Add(new OidcProviderEntity
            {
                Id = providerId,
                Name = "Test provider",
                IssuerUrl = "https://idp.invalid",
                ClientId = "client",
                IsEnabled = isEnabled,
            });
        }

        var subjectId = Guid.CreateVersion7();
        _context.Subjects.Add(new SubjectEntity { Id = subjectId, Name = "alice" });
        _context.SubjectOidcIdentities.Add(new SubjectOidcIdentityEntity
        {
            Id = Guid.CreateVersion7(),
            SubjectId = subjectId,
            ProviderId = providerId,
            Issuer = "https://idp.invalid",
            OidcSubjectId = $"ext-{subjectId:N}",
        });
        await _context.SaveChangesAsync();
        return subjectId;
    }

    [Fact]
    public async Task An_identity_on_an_enabled_provider_is_working()
    {
        var subjectId = await SeedIdentityAsync(providerExists: true, isEnabled: true);

        (await _context.WorkingOidcIdentities().AnyAsync(i => i.SubjectId == subjectId))
            .Should().BeTrue();
    }

    [Fact]
    public async Task An_identity_on_a_disabled_provider_is_not_working()
    {
        var subjectId = await SeedIdentityAsync(providerExists: true, isEnabled: false);

        (await _context.WorkingOidcIdentities().AnyAsync(i => i.SubjectId == subjectId))
            .Should().BeFalse();
    }

    [Fact]
    public async Task An_identity_with_no_provider_row_is_not_working()
    {
        var subjectId = await SeedIdentityAsync(providerExists: false, isEnabled: true);

        (await _context.WorkingOidcIdentities().AnyAsync(i => i.SubjectId == subjectId))
            .Should().BeFalse();
    }
}

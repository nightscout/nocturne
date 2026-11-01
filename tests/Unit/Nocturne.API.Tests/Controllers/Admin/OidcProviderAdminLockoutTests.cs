using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nocturne.API.Controllers.V4.TenantAdmin;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

using Nocturne.Infrastructure.Data.Repositories;

using Microsoft.AspNetCore.Http;

namespace Nocturne.API.Tests.Controllers.Admin;

/// <summary>
/// A provider cannot be taken away from accounts that have no other way to sign in, by any of the
/// three routes that switch it off.
/// </summary>
[Trait("Category", "Unit")]
public sealed class OidcProviderAdminLockoutTests : IDisposable
{
    private readonly SqliteTestDatabase _db = TestDbContextFactory.CreateSqlite();
    private readonly NocturneDbContext _context;
    private readonly Mock<IOidcProviderService> _providers = new();
    private readonly OidcProviderAdminController _controller;

    private readonly Guid _google = Guid.CreateVersion7();
    private readonly Guid _github = Guid.CreateVersion7();

    public OidcProviderAdminLockoutTests()
    {
        _context = _db.CreateContext();
        _context.OidcProviders.Add(Provider(_google, "https://accounts.google.com"));
        _context.OidcProviders.Add(Provider(_github, "https://github.com"));
        _context.SaveChanges();

        _providers.Setup(p => p.GetProviderByIdAsync(_google)).ReturnsAsync(new OidcProvider
        {
            Id = _google, Name = "Google", IssuerUrl = "https://accounts.google.com", ClientId = "c", IsEnabled = true,
        });
        _providers.Setup(p => p.DisableProviderAsync(_google)).ReturnsAsync(true);
        _providers.Setup(p => p.DeleteProviderAsync(_google)).ReturnsAsync(true);
        _providers.Setup(p => p.UpdateProviderAsync(It.IsAny<OidcProvider>()))
            .ReturnsAsync((OidcProvider p) => p);

        _controller = new OidcProviderAdminController(
            _providers.Object, new SubjectRepository(_context.AsFactory()), Mock.Of<IHttpClientFactory>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
    }

    private static OidcProviderEntity Provider(Guid id, string issuer) => new()
    {
        Id = id, Name = issuer, IssuerUrl = issuer, ClientId = "c", IsEnabled = true,
    };

    private Guid SeedSubject(bool passkey = false, params Guid[] providers) =>
        SeedSubject(passkey, demo: false, providers);

    private Guid SeedSubject(bool passkey, bool demo, params Guid[] providers)
    {
        var id = Guid.CreateVersion7();
        _context.Subjects.Add(new SubjectEntity { Id = id, Name = "s" + id, IsActive = true, IsDemoSubject = demo });
        foreach (var provider in providers)
        {
            _context.SubjectOidcIdentities.Add(new SubjectOidcIdentityEntity
            {
                Id = Guid.CreateVersion7(), SubjectId = id, ProviderId = provider,
                OidcSubjectId = "ext-" + id, Issuer = provider.ToString(), LinkedAt = DateTime.UtcNow,
            });
        }

        if (passkey)
        {
            _context.PasskeyCredentials.Add(new PasskeyCredentialEntity
            {
                Id = Guid.CreateVersion7(), SubjectId = id, CredentialId = id.ToByteArray(), PublicKey = [1],
                CreatedAt = DateTime.UtcNow,
            });
        }

        _context.SaveChanges();
        return id;
    }

    private static UpdateOidcProviderRequest Disabling() =>
        new("Google", "https://accounts.google.com", "c", IsEnabled: false);

    [Fact]
    public async Task Disabling_a_provider_someone_relies_on_alone_is_refused_even_with_another_provider_enabled()
    {
        SeedSubject(providers: _google);

        (await _controller.Disable(_google)).Should().BeOfType<ConflictObjectResult>();
        _providers.Verify(p => p.DisableProviderAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task Turning_a_provider_off_through_an_update_is_refused_the_same_way()
    {
        SeedSubject(providers: _google);

        (await _controller.Update(_google, Disabling())).Should().BeOfType<ConflictObjectResult>();
        _providers.Verify(p => p.UpdateProviderAsync(It.IsAny<OidcProvider>()), Times.Never);
    }

    [Fact]
    public async Task Deleting_a_provider_someone_relies_on_alone_is_refused()
    {
        SeedSubject(providers: _google);

        (await _controller.Delete(_google)).Should().BeOfType<ConflictObjectResult>();
        _providers.Verify(p => p.DeleteProviderAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task A_provider_whose_users_all_have_another_way_in_can_be_disabled()
    {
        SeedSubject(passkey: true, _google);
        SeedSubject(false, _google, _github);

        (await _controller.Disable(_google)).Should().BeOfType<NoContentResult>();
        (await _controller.Update(_google, Disabling())).Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Force_switches_the_provider_off_anyway()
    {
        SeedSubject(providers: _google);

        (await _controller.Disable(_google, force: true)).Should().BeOfType<NoContentResult>();
        (await _controller.Delete(_google, force: true)).Should().BeOfType<NoContentResult>();
        (await _controller.Update(_google, Disabling(), force: true)).Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task A_demo_visitor_is_not_an_account_to_strand()
    {
        SeedSubject(false, demo: true, _google);

        (await _controller.Disable(_google)).Should().BeOfType<NoContentResult>();
    }
}

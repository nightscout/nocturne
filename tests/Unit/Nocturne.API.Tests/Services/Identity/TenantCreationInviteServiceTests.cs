using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Identity;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Repositories;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Identity;

public class TenantCreationInviteServiceTests : IDisposable
{
    private const string BaseDomain = "nocturne.example";

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
    private readonly Mock<ITenantService> _tenantService = new();
    private readonly Mock<IJwtService> _jwt = new();
    private readonly Guid _adminId = Guid.CreateVersion7();
    private int _tokens;

    public TenantCreationInviteServiceTests()
    {
        _db = TestDbContextFactory.CreateSqlite();
        _context = _db.CreateContext();
        _context.Subjects.Add(new SubjectEntity { Id = _adminId, Name = "Admin" });
        _context.SaveChanges();

        _jwt.Setup(j => j.GenerateRefreshToken()).Returns(() => $"token-{++_tokens}");
        _tenantService
            .Setup(t => t.ValidateSlugAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SlugValidationResult(true));
        _tenantService
            .Setup(t => t.CreateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string slug, string name, Guid _, CancellationToken _) => AddTenant(slug, name));
    }

    public void Dispose() => _db.Dispose();

    private TenantCreationInviteService Service(string baseDomain = BaseDomain) =>
        new(
            new TenantCreationInviteRepository(_context.AsFactory()),
            new TenantRepository(_context.AsFactory()),
            _tenantService.Object,
            new TenantMembershipRepository(_context.AsFactory()),
            _jwt.Object,
            Options.Create(new BaseDomainOptions { BaseDomain = baseDomain }),
            _time,
            NullLogger<TenantCreationInviteService>.Instance);

    private TenantCreatedDto AddTenant(string slug, string name)
    {
        var tenant = new TenantEntity { Id = Guid.CreateVersion7(), Slug = slug, DisplayName = name, IsActive = true };
        _context.Tenants.Add(tenant);
        _context.SaveChanges();
        return new TenantCreatedDto(tenant.Id, tenant.Slug, tenant.DisplayName, true, DateTime.UtcNow);
    }

    private async Task<string> CreateLinkAsync(int? maxUses = 1, int days = 7)
    {
        var outcome = await Service().CreateAsync(
            _adminId, new CreateTenantCreationInviteRequest(null, maxUses, days, ConfirmSubdomainChange: true), default);
        outcome.Refusal.Should().BeNull();
        return new Uri(outcome.Value!.Url).Query.Split('=')[1];
    }

    [Fact]
    public async Task While_one_tenant_serves_the_bare_domain_a_link_needs_the_subdomain_confirmation()
    {
        AddTenant("rhys", "Rhys");

        var overview = await Service().GetOverviewAsync(default);
        overview.SubdomainNotice.Should().NotBeNull();
        overview.SubdomainNotice!.TenantAddress.Should().Be("https://rhys.nocturne.example");
        overview.SubdomainNotice.BareAddress.Should().Be("https://nocturne.example");

        var refused = await Service().CreateAsync(
            _adminId, new CreateTenantCreationInviteRequest(null, 1, 7), default);
        refused.Refusal!.Code.Should().Be(TenantInviteRefusalCode.SubdomainChangeUnconfirmed);

        var made = await Service().CreateAsync(
            _adminId, new CreateTenantCreationInviteRequest(null, 1, 7, ConfirmSubdomainChange: true), default);
        made.Refusal.Should().BeNull();
        made.Value!.Url.Should().StartWith("https://nocturne.example/start?token=");
    }

    [Fact]
    public async Task Once_two_tenants_exist_no_confirmation_is_needed()
    {
        AddTenant("one", "One");
        AddTenant("two", "Two");

        (await Service().GetOverviewAsync(default)).SubdomainNotice.Should().BeNull();
        var made = await Service().CreateAsync(
            _adminId, new CreateTenantCreationInviteRequest(null, 1, 7), default);
        made.Refusal.Should().BeNull();
    }

    [Fact]
    public async Task Without_a_base_domain_no_link_is_made()
    {
        var outcome = await Service(baseDomain: "").CreateAsync(
            _adminId, new CreateTenantCreationInviteRequest(null, 1, 7, true), default);
        outcome.Refusal!.Code.Should().Be(TenantInviteRefusalCode.BaseDomainRequired);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(91, 1)]
    [InlineData(7, 0)]
    public async Task Out_of_range_expiry_or_cap_is_refused(int days, int maxUses)
    {
        var outcome = await Service().CreateAsync(
            _adminId, new CreateTenantCreationInviteRequest(null, maxUses, days, true), default);
        outcome.Refusal!.Code.Should().Be(TenantInviteRefusalCode.InvalidRequest);
    }

    [Fact]
    public async Task A_single_use_link_creates_one_tenant_and_then_reads_as_used()
    {
        var token = await CreateLinkAsync(maxUses: 1);
        var owner = Guid.CreateVersion7();

        var claim = await Service().ClaimAsync(token, default);
        claim.Refusal.Should().BeNull();

        var created = await Service().CreateTenantAsync(claim.Value, "sam", "Sam", owner, null, default);
        created.Refusal.Should().BeNull();
        created.Value!.Address.Should().Be("https://sam.nocturne.example");

        (await Service().GetInfoAsync(token, default)).Refusal!.Code.Should().Be(TenantInviteRefusalCode.UsedUp);
        (await Service().ClaimAsync(token, default)).Refusal!.Code.Should().Be(TenantInviteRefusalCode.UsedUp);

        var listed = (await Service().GetOverviewAsync(default)).Invites.Single();
        listed.Status.Should().Be(TenantCreationInviteStatus.UsedUp);
        listed.Uses.Should().ContainSingle().Which.Slug.Should().Be("sam");
    }

    [Fact]
    public async Task Two_claims_on_a_single_use_link_cannot_both_succeed()
    {
        var token = await CreateLinkAsync(maxUses: 1);

        var first = await Service().ClaimAsync(token, default);
        var second = await Service().ClaimAsync(token, default);

        first.Refusal.Should().BeNull();
        second.Refusal!.Code.Should().Be(TenantInviteRefusalCode.UsedUp);
    }

    [Fact]
    public async Task A_refused_subdomain_gives_the_use_back()
    {
        var token = await CreateLinkAsync(maxUses: 1);
        _tenantService
            .Setup(t => t.ValidateSlugAsync("taken", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SlugValidationResult(false, "This name is already taken"));

        var claim = await Service().ClaimAsync(token, default);
        var created = await Service().CreateTenantAsync(claim.Value, "taken", "Sam", Guid.CreateVersion7(), null, default);

        created.Refusal!.Code.Should().Be(TenantInviteRefusalCode.InvalidSlug);
        (await Service().ClaimAsync(token, default)).Refusal.Should().BeNull();
    }

    [Fact]
    public async Task A_link_with_no_cap_keeps_working()
    {
        var token = await CreateLinkAsync(maxUses: null);

        for (var i = 0; i < 3; i++)
        {
            var claim = await Service().ClaimAsync(token, default);
            claim.Refusal.Should().BeNull();
            (await Service().CreateTenantAsync(claim.Value, $"site{i}", "Site", Guid.CreateVersion7(), null, default))
                .Refusal.Should().BeNull();
        }

        (await Service().GetOverviewAsync(default)).Invites.Single().UseCount.Should().Be(3);
    }

    [Fact]
    public async Task Revoked_and_expired_links_are_refused_with_their_reason()
    {
        var revokedToken = await CreateLinkAsync();
        var expiringToken = await CreateLinkAsync(days: 1);

        var revokedHash = HashUtils.Sha256Hex(revokedToken);
        var revokedId = (await _context.TenantCreationInvites.AsNoTracking().SingleAsync(i => i.TokenHash == revokedHash)).Id;
        (await Service().RevokeAsync(revokedId, default)).Should().BeTrue();
        (await Service().ClaimAsync(revokedToken, default)).Refusal!.Code.Should().Be(TenantInviteRefusalCode.Revoked);

        _time.Advance(TimeSpan.FromDays(2));
        (await Service().ClaimAsync(expiringToken, default)).Refusal!.Code.Should().Be(TenantInviteRefusalCode.Expired);
    }

    [Fact]
    public async Task An_unknown_token_is_not_found()
    {
        (await Service().GetInfoAsync("nope", default)).Refusal!.Code.Should().Be(TenantInviteRefusalCode.NotFound);
    }

    [Fact]
    public async Task The_token_itself_is_never_stored()
    {
        var token = await CreateLinkAsync();
        var stored = await _context.TenantCreationInvites.AsNoTracking().SingleAsync();
        stored.TokenHash.Should().Be(HashUtils.Sha256Hex(token)).And.NotBe(token);
    }
}

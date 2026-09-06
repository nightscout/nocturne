using System.Linq;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Multitenancy;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Security;
using Xunit;

namespace Nocturne.API.Tests.Multitenancy;

/// <summary>
/// Verifies <see cref="TenantResolutionMiddleware"/> resolves the public share host
/// <c>{token}.share.{baseDomain}</c> by token and marks the request read-only-public, while the
/// bare <c>{slug}.{baseDomain}</c> host (including grandfathered hyphenated slugs) keeps resolving
/// normally with no share access.
/// </summary>
public sealed class TenantResolutionMiddlewareShareTokenTests : TenantResolutionMiddlewareTestBase
{
    private const string Slug = "acme";
    private const string ShareToken = "k7m2q9x4r3wt";

    private readonly Guid _tenantId;

    public TenantResolutionMiddlewareShareTokenTests() => _tenantId = SeedTenant(new TenantEntity
    {
        Slug = Slug,
        DisplayName = "Acme",
        // The column holds the token's digest; the host still carries the token itself.
        ShareToken = CredentialHash.ShareToken(ShareToken),
        ShareTokenSetAt = DateTime.UtcNow,
    });

    [Fact]
    public async Task Valid_share_host_resolves_tenant_and_marks_share_access()
    {
        var (ctx, nextCalled) = await InvokeAsync($"{ShareToken}.share.{BaseDomain}");

        nextCalled.Should().BeTrue();
        ((bool)ctx.Items["ShareAccess"]!).Should().BeTrue();
        (ctx.Items["TenantContext"] as TenantContext)!.TenantId.Should().Be(_tenantId);
        // The RLS carrier is marked pre-auth on both the request context and the pinned DbContext.
        Resolve<ICategoryReadContext>(ctx).IsShare.Should().BeTrue();
        Resolve<NocturneDbContext>(ctx).IsShareContext.Should().BeTrue();
    }

    [Fact]
    public async Task Unknown_share_token_returns_404_and_no_share_access()
    {
        var (ctx, nextCalled) = await InvokeAsync($"deadbeef0000.share.{BaseDomain}");

        nextCalled.Should().BeFalse();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        ctx.Items.ContainsKey("ShareAccess").Should().BeFalse();
    }

    [Fact]
    public async Task Bare_slug_host_resolves_tenant_without_share_access()
    {
        var (ctx, nextCalled) = await InvokeAsync($"{Slug}.{BaseDomain}");

        nextCalled.Should().BeTrue();
        ctx.Items.ContainsKey("ShareAccess").Should().BeFalse();
        (ctx.Items["TenantContext"] as TenantContext)!.TenantId.Should().Be(_tenantId);
        // A bare slug host is not a share: the carrier stays off, so RLS opens for the owner.
        Resolve<ICategoryReadContext>(ctx).IsShare.Should().BeFalse();
        Resolve<NocturneDbContext>(ctx).IsShareContext.Should().BeFalse();
    }

    [Fact]
    public async Task Inactive_tenant_via_share_token_returns_403()
    {
        using (var db = Db())
        {
            db.Tenants.Single(t => t.Id == _tenantId).IsActive = false;
            db.SaveChanges();
        }

        var (ctx, nextCalled) = await InvokeAsync($"{ShareToken}.share.{BaseDomain}");

        nextCalled.Should().BeFalse();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task Uppercase_token_in_host_still_resolves()
    {
        // Hostnames are case-insensitive; generated tokens are lowercase, so an upper-cased link must work.
        var (ctx, nextCalled) = await InvokeAsync($"{ShareToken.ToUpperInvariant()}.share.{BaseDomain}");

        nextCalled.Should().BeTrue();
        ((bool)ctx.Items["ShareAccess"]!).Should().BeTrue();
        (ctx.Items["TenantContext"] as TenantContext)!.TenantId.Should().Be(_tenantId);
    }

    [Fact]
    public async Task Slug_ending_in_share_is_not_a_share_host()
    {
        // "mathshare" ends in "share" but the share form requires a ".share" label boundary.
        SeedTenant("mathshare");

        var (ctx, nextCalled) = await InvokeAsync($"mathshare.{BaseDomain}");

        nextCalled.Should().BeTrue();
        ctx.Items.ContainsKey("ShareAccess").Should().BeFalse();
        (ctx.Items["TenantContext"] as TenantContext)!.Slug.Should().Be("mathshare");
    }

    [Fact]
    public async Task Bare_share_label_host_returns_404()
    {
        // share.{baseDomain} has no token and "share" is a reserved slug — no tenant, generic 404.
        var (ctx, nextCalled) = await InvokeAsync($"share.{BaseDomain}");

        nextCalled.Should().BeFalse();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        ctx.Items.ContainsKey("ShareAccess").Should().BeFalse();
    }

    [Fact]
    public async Task Hyphenated_slug_is_not_parsed_as_a_share_host()
    {
        SeedTenant("as-notrune");

        var (ctx, nextCalled) = await InvokeAsync($"as-notrune.{BaseDomain}");

        nextCalled.Should().BeTrue();
        ctx.Items.ContainsKey("ShareAccess").Should().BeFalse();
        (ctx.Items["TenantContext"] as TenantContext)!.Slug.Should().Be("as-notrune");
    }
}

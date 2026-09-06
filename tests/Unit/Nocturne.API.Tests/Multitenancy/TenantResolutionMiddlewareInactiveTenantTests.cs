using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Nocturne.API.Multitenancy;
using Nocturne.Core.Contracts.Multitenancy;
using Xunit;

namespace Nocturne.API.Tests.Multitenancy;

/// <summary>
/// An inactive tenant's subdomain in <see cref="TenantResolutionMiddleware"/>. The refusal has to
/// name itself, because the person on the other side of it is usually the account holder and the
/// web app can only explain what it can recognise; and it has to leave the liveness probes and the
/// operator's address answering, because a probe cannot otherwise tell a suspended deployment from
/// a broken one and the page explaining the refusal has nowhere to point.
/// </summary>
[Trait("Category", "Unit")]
public sealed class TenantResolutionMiddlewareInactiveTenantTests : TenantResolutionMiddlewareTestBase
{
    private const string Slug = "lapsed";

    protected override string BaseDomain => "nocturne.example";

    public TenantResolutionMiddlewareInactiveTenantTests() => SeedTenant(Slug, isActive: false);

    private Task<(DefaultHttpContext Context, bool NextCalled)> OnTheLapsedHost(
        string path, string method = "GET") =>
        InvokeAsync($"{Slug}.{BaseDomain}", path, method);

    [Theory]
    [InlineData("/api/v4/sensorglucose")]
    [InlineData("/api/v4/status")]
    [InlineData("/api/auth/oidc/session")]
    public async Task Refuses_with_a_code_the_web_app_can_recognise(string path)
    {
        var (context, nextCalled) = await OnTheLapsedHost(path);

        nextCalled.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        JsonDocument.Parse(await ReadBodyAsync(context)).RootElement.GetProperty("error").GetString()
            .Should().Be(TenantResolutionMiddleware.TenantInactiveCode);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/alive")]
    public async Task Liveness_probes_answer_without_resolving_the_tenant(string path)
    {
        var (context, nextCalled) = await OnTheLapsedHost(path);

        nextCalled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        Resolve<ITenantAccessor>(context).IsResolved.Should().BeFalse();
    }

    [Fact]
    public async Task The_operators_address_is_readable_so_the_refusal_can_be_explained()
    {
        var (context, nextCalled) = await OnTheLapsedHost("/api/v4/support/config");

        nextCalled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task Only_the_method_the_slice_names_is_served()
    {
        var (context, nextCalled) = await OnTheLapsedHost("/api/v4/support/config", HttpMethods.Post);

        nextCalled.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    /// <summary>
    /// The inactive slice is a subset of the tenantless list, not a second list that could drift
    /// into admitting something the tenantless surface itself does not.
    /// </summary>
    [Fact]
    public void Every_inactive_entry_is_also_tenantless_allowed()
    {
        TenantResolutionMiddleware.InactiveTenantPaths.Should().NotBeEmpty();

        foreach (var entry in TenantResolutionMiddleware.InactiveTenantPaths)
        {
            TenantResolutionMiddleware.IsTenantlessAllowed(entry.Path, entry.Method)
                .Should().BeTrue("{0} is served on an inactive tenant's host but is not on the "
                    + "tenantless list it is meant to be a slice of", entry.Path);
        }
    }
}

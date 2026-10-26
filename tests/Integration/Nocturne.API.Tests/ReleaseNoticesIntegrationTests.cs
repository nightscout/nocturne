using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Services.Platform.Releases;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Models;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

[Trait("Category", "Integration")]
public class ReleaseNoticesIntegrationTests : ApiIntegrationTestBase
{
    public ReleaseNoticesIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    [Fact]
    public async Task TheAppRoleClaimsANoticeOnce()
    {
        var ledger = Fixture.Services.GetRequiredService<IReleaseNoticeLedger>();
        var version = $"9.9.{Random.Shared.Next(1_000_000)}";

        (await ledger.TryClaimAsync(ReleaseNoticeLedger.Available, version, CancellationToken.None)).Should().BeTrue();
        (await ledger.TryClaimAsync(ReleaseNoticeLedger.Available, version, CancellationToken.None)).Should().BeFalse();
        (await ledger.TryClaimAsync(ReleaseNoticeLedger.Installed, version, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task TheOwnerReadsTheReleaseStatus()
    {
        var response = await AuthenticatedClient.GetAsync("/api/v4/releases/status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var status = await response.Content.ReadFromJsonAsync<ReleaseStatusDto>();
        status!.IsReleaseBuild.Should().BeFalse("a test build is not stamped from a release tag");
        status.UpdateAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task AnAnonymousCallerCannotReadTheReleaseStatus()
    {
        var response = await ApiClient.GetAsync("/api/v4/releases/status");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }
}

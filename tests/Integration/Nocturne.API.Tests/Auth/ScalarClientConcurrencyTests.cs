using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Services.Docs;
using Nocturne.API.Tests.Integration.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Auth;

/// <summary>
/// First requests for one new tenant's docs page arriving together. No unique index stops them each
/// inserting a Scalar client, so only the per-tenant lock leaves the tenant one row.
/// </summary>
[Trait("Category", "Integration")]
public class ScalarClientConcurrencyTests : ApiIntegrationTestBase
{
    private const int Racers = 16;

    public ScalarClientConcurrencyTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    [Fact]
    public async Task EnsureScalarClientAsync_ConcurrentFirstRequests_LeaveOneRow()
    {
        var slug = $"scalar-{Guid.NewGuid():N}"[..24];
        var tenantId = await AuthTestHelpers.SeedTenantAsync(Fixture, slug, slug);
        var redirectUri = $"https://{slug}.localhost/scalar/{Guid.NewGuid():N}";
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var racers = Enumerable.Range(0, Racers).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            using var scope = Fixture.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<ScalarAuthProvider>()
                .EnsureScalarClientAsync(tenantId, redirectUri, CancellationToken.None);
        })).ToList();
        start.SetResult();
        var clientIds = await Task.WhenAll(racers);

        clientIds.Distinct().Should().ContainSingle().Which.Should().NotBeNull();
        await using var db = Fixture.CreateDbContext(tenantId);
        (await db.OAuthClients.CountAsync(c => c.SoftwareId == ScalarAuthProvider.ScalarSoftwareId))
            .Should().Be(1);
    }
}

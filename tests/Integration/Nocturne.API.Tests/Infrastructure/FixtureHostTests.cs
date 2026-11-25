using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nocturne.API.Services.Jobs;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration.Infrastructure;

[Collection(ApiIntegrationShards.One)]
[Trait("Category", "Integration")]
public class FixtureHostTests : ApiIntegrationTestBase
{
    public FixtureHostTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    /// <summary>See <see cref="ApiIntegrationTestFixture.RemoveJobScheduler"/>.</summary>
    [Fact]
    public void The_host_runs_no_job_scheduler_to_write_while_a_cleanup_truncates()
    {
        Fixture.Services.GetServices<IHostedService>().Should().NotContain(s => s is JobScheduler);
    }
}

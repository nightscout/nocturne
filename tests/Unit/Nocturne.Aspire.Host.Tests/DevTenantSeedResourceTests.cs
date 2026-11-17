using System.Net;
using System.Net.Sockets;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Nocturne.Aspire.Host.Tests;

public class DevTenantSeedResourceTests
{
    [Fact]
    public async Task A_failed_seed_still_finishes_with_exit_code_0()
    {
        var builder = DistributedApplication.CreateBuilder();
        var api = builder.AddContainer("api", "api-image").WithHttpEndpoint(name: "http");
        var seed = builder.AddDevTenantSeed("dev-seed", api, sampleDataDays: 30);

        // Nothing listens on the port, so the seed fails to connect.
        var endpoint = api.Resource.Annotations.OfType<EndpointAnnotation>().Single();
        endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", UnusedPort());

        await using var app = builder.Build();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var eventing = app.Services.GetRequiredService<IDistributedApplicationEventing>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await eventing.PublishAsync(
            new InitializeResourceEvent(
                seed.Resource,
                eventing,
                app.Services.GetRequiredService<ResourceLoggerService>(),
                notifications,
                app.Services),
            timeout.Token);

        var finished = await notifications.WaitForResourceAsync(
            seed.Resource.Name,
            e => KnownResourceStates.TerminalStates.Contains(e.Snapshot.State?.Text),
            timeout.Token);
        finished.Snapshot.State?.Text.Should().Be(KnownResourceStates.Finished);
        finished.Snapshot.ExitCode.Should().Be(0);
    }

    /// <summary>
    /// The orchestrator runs the wait on the API as a <see cref="BeforeResourceStartedEvent"/>
    /// subscriber, which throws once the API can no longer become healthy.
    /// </summary>
    [Fact]
    public async Task An_api_that_never_comes_up_ends_the_seed_as_failed_to_start()
    {
        var builder = DistributedApplication.CreateBuilder();
        var api = builder.AddContainer("api", "api-image").WithHttpEndpoint(name: "http");
        var seed = builder.AddDevTenantSeed("dev-seed", api, sampleDataDays: 30);
        builder.Eventing.Subscribe<BeforeResourceStartedEvent>(seed.Resource, (_, _) =>
            throw new DistributedApplicationException("Resource 'api' has failed to start."));

        var ended = await InitializeAndWaitForTerminalStateAsync(builder, seed);

        ended.State?.Text.Should().Be(KnownResourceStates.FailedToStart);
        ended.ExitCode.Should().NotBe(0);
    }

    [Fact]
    public async Task A_fault_after_the_start_ends_the_seed_as_exited()
    {
        var builder = DistributedApplication.CreateBuilder();
        // Never allocated, so reading its URL throws.
        var api = builder.AddContainer("api", "api-image").WithHttpEndpoint(name: "http");
        var seed = builder.AddDevTenantSeed("dev-seed", api, sampleDataDays: 30);

        var ended = await InitializeAndWaitForTerminalStateAsync(builder, seed);

        ended.State?.Text.Should().Be(KnownResourceStates.Exited);
        ended.ExitCode.Should().NotBe(0);
    }

    [Fact]
    public void The_seed_waits_for_the_api()
    {
        var builder = DistributedApplication.CreateBuilder();
        var api = builder.AddContainer("api", "api-image").WithHttpEndpoint(name: "http");

        var seed = builder.AddDevTenantSeed("dev-seed", api, sampleDataDays: 30);

        seed.Resource.Annotations.OfType<WaitAnnotation>().Should().ContainSingle(w =>
            w.Resource == api.Resource && w.WaitType == WaitType.WaitUntilHealthy);
    }

    private static async Task<CustomResourceSnapshot> InitializeAndWaitForTerminalStateAsync(
        IDistributedApplicationBuilder builder, IResourceBuilder<DevTenantSeedResource> seed)
    {
        await using var app = builder.Build();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var eventing = app.Services.GetRequiredService<IDistributedApplicationEventing>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var initialize = eventing.PublishAsync(
            new InitializeResourceEvent(
                seed.Resource,
                eventing,
                app.Services.GetRequiredService<ResourceLoggerService>(),
                notifications,
                app.Services),
            timeout.Token);

        var ended = await notifications.WaitForResourceAsync(
            seed.Resource.Name,
            e => KnownResourceStates.TerminalStates.Contains(e.Snapshot.State?.Text),
            timeout.Token);
        await FluentActions.Awaiting(() => initialize).Should().ThrowAsync<Exception>();
        return ended.Snapshot;
    }

    private static int UnusedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

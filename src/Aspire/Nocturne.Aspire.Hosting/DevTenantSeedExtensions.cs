using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// A one-shot that runs <see cref="DevTenantSeeder"/> in the AppHost once the API is healthy and
/// then finishes with exit code 0 whether or not the seed went through, so a resource that waits
/// for its completion starts either way.
/// </summary>
public sealed class DevTenantSeedResource(string name) : Resource(name), IResourceWithWaitSupport;

public static class DevTenantSeedExtensions
{
    public static IResourceBuilder<DevTenantSeedResource> AddDevTenantSeed(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        IResourceBuilder<IResourceWithEndpoints> api,
        int sampleDataDays)
    {
        return builder
            .AddResource(new DevTenantSeedResource(name))
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "DevTenantSeed",
                State = KnownResourceStates.NotStarted,
                Properties = [],
            })
            .WaitFor(api)
            .ExcludeFromManifest()
            .OnInitializeResource(async (resource, e, ct) =>
            {
                // Aspire drops an exception thrown from this handler and leaves the resource in
                // Waiting, which would hold the web app in Waiting with no error. Whatever the
                // handler did not finish ends in a terminal state: FailedToStart when the API
                // never came up (the web app then fails as it would waiting on the API itself).
                var started = false;
                var finished = false;
                try
                {
                    // Runs the WaitFor above; throws once the API can no longer become healthy.
                    await e.Eventing.PublishAsync(new BeforeResourceStartedEvent(resource, e.Services), ct);

                    await e.Notifications.PublishUpdateAsync(resource, s => s with
                    {
                        State = KnownResourceStates.Running,
                        StartTimeStamp = DateTime.UtcNow,
                    });
                    started = true;

                    using var http = new HttpClient
                    {
                        BaseAddress = new Uri(api.GetEndpoint("http").Url),
                        Timeout = TenantSeedClient.Timeout,
                    };
                    await new DevTenantSeeder(http, e.Logger).RunAsync(sampleDataDays, ct);

                    await e.Notifications.PublishUpdateAsync(resource, s => s with
                    {
                        State = KnownResourceStates.Finished,
                        ExitCode = 0,
                        StopTimeStamp = DateTime.UtcNow,
                    });
                    finished = true;
                }
                finally
                {
                    if (!finished)
                    {
                        await e.Notifications.PublishUpdateAsync(resource, s => s with
                        {
                            State = started ? KnownResourceStates.Exited : KnownResourceStates.FailedToStart,
                            ExitCode = 1,
                            StopTimeStamp = DateTime.UtcNow,
                        });
                    }
                }
            });
    }
}

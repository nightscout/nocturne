using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using FluentAssertions;
using Nocturne.Aspire.Hosting;
using Xunit;

namespace Nocturne.Aspire.Host.Tests;

public class DemoServiceExtensionsTests
{
    [Theory]
    [InlineData("true", "true")]
    [InlineData("false", "false")]
    public async Task The_api_is_told_whether_demo_mode_is_on(string enabled, string expected)
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.Configuration["Parameters:DemoMode:Enabled"] = enabled;
        builder.Configuration["Parameters:instance-key"] = "key";
        var api = builder.AddContainer("api", "api-image").WithHttpEndpoint(name: "http");
        var instanceKey = builder.AddParameter("instance-key", secret: true);

        builder.AddDemoService<Projects.Nocturne_Services_Demo>(api, null, instanceKey);

        var environment = await EnvironmentOfAsync(api.Resource);
        environment["DemoService__Enabled"].Should().Be(expected);
    }

    /// <summary>Runs the resource's environment callbacks without resolving endpoint references, which are not allocated here.</summary>
    private static async Task<Dictionary<string, object>> EnvironmentOfAsync(IResource resource)
    {
        var context = new EnvironmentCallbackContext(
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run));
        foreach (var callback in resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await callback.Callback(context);
        }

        return context.EnvironmentVariables;
    }
}

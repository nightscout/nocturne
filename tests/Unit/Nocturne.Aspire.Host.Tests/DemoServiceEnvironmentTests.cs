using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using FluentAssertions;
using Nocturne.Aspire.Hosting;
using Xunit;

namespace Nocturne.Aspire.Host.Tests;

public class DemoServiceEnvironmentTests
{
    private static readonly string[] Intervals =
    [
        "DemoMode__BackfillDays",
        "DemoMode__IntervalMinutes",
        "DemoMode__ResetIntervalMinutes",
        "DemoMode__VisitorResetIntervalMinutes",
    ];

    [Fact]
    public async Task Values_nobody_configured_are_left_to_the_demo_services_own_defaults()
    {
        var environment = await DemoEnvironmentAsync(new Dictionary<string, string?>());

        environment.Keys.Should().NotContain(Intervals);
        environment["DemoMode__Enabled"].Should().Be("true");
    }

    [Fact]
    public async Task Configured_values_reach_the_demo_service()
    {
        var environment = await DemoEnvironmentAsync(new Dictionary<string, string?>
        {
            ["BackfillDays"] = "14",
            ["IntervalMinutes"] = "1",
            ["ResetIntervalMinutes"] = "0",
            ["VisitorResetIntervalMinutes"] = "30",
        });

        environment["DemoMode__BackfillDays"].Should().Be("14");
        environment["DemoMode__IntervalMinutes"].Should().Be("1");
        environment["DemoMode__ResetIntervalMinutes"].Should().Be("0");
        environment["DemoMode__VisitorResetIntervalMinutes"].Should().Be("30");
    }

    [Fact]
    public async Task A_value_set_by_the_caller_reaches_the_demo_service_and_leaves_the_rest_unset()
    {
        var environment = await DemoEnvironmentAsync(
            new Dictionary<string, string?>(), options => options.IntervalMinutes = 2);

        environment["DemoMode__IntervalMinutes"].Should().Be("2");
        environment.Keys.Should().NotContain(
            ["DemoMode__BackfillDays", "DemoMode__ResetIntervalMinutes", "DemoMode__VisitorResetIntervalMinutes"]);
    }

    private static async Task<Dictionary<string, object>> DemoEnvironmentAsync(
        Dictionary<string, string?> demoMode, Action<DemoServiceOptions>? configure = null)
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.Configuration["Parameters:DemoMode:Enabled"] = "true";
        foreach (var (key, value) in demoMode)
            builder.Configuration[$"Parameters:DemoMode:{key}"] = value;

        var api = builder.AddContainer("api", "api-image").WithHttpEndpoint(name: "http");
        var instanceKey = builder.AddParameter("instance-key", secret: true);

        var demo = builder.AddDemoService<Projects.Nocturne_Services_Demo>(api, null, instanceKey, configure);

        var environment = new Dictionary<string, object>();
        var context = new EnvironmentCallbackContext(
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run), environment);
        foreach (var annotation in demo!.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
            await annotation.Callback(context);
        return environment;
    }
}

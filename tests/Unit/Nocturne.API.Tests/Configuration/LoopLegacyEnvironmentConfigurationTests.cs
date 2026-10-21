using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nocturne.API.Extensions;
using Nocturne.Core.Models;

namespace Nocturne.API.Tests.Configuration;

public class LoopLegacyEnvironmentConfigurationTests
{
    private static LoopConfiguration Resolve(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddRealTimeAndNotifications(configuration);
        return services.BuildServiceProvider().GetRequiredService<IOptions<LoopConfiguration>>().Value;
    }

    [Fact]
    public void LegacyVariables_BindWhenSectionIsEmpty()
    {
        var options = Resolve(
            new()
            {
                ["LOOP_APNS_KEY"] = "legacy-key",
                ["LOOP_APNS_KEY_ID"] = "LEGACYKEYID",
                ["LOOP_DEVELOPER_TEAM_ID"] = "LEGACYTEAM",
                ["LOOP_PUSH_SERVER_ENVIRONMENT"] = "development",
            }
        );

        Assert.Equal("legacy-key", options.ApnsKey);
        Assert.Equal("LEGACYKEYID", options.ApnsKeyId);
        Assert.Equal("LEGACYTEAM", options.DeveloperTeamId);
        Assert.Equal("development", options.PushServerEnvironment);
    }

    [Fact]
    public void SectionValues_WinOverLegacyVariables()
    {
        var options = Resolve(
            new()
            {
                ["Loop:ApnsKey"] = "section-key",
                ["Loop:ApnsKeyId"] = "SECTIONKEY",
                ["Loop:DeveloperTeamId"] = "SECTIONTEA",
                ["Loop:PushServerEnvironment"] = "production",
                ["LOOP_APNS_KEY"] = "legacy-key",
                ["LOOP_APNS_KEY_ID"] = "LEGACYKEYID",
                ["LOOP_DEVELOPER_TEAM_ID"] = "LEGACYTEAM",
                ["LOOP_PUSH_SERVER_ENVIRONMENT"] = "development",
            }
        );

        Assert.Equal("section-key", options.ApnsKey);
        Assert.Equal("SECTIONKEY", options.ApnsKeyId);
        Assert.Equal("SECTIONTEA", options.DeveloperTeamId);
        Assert.Equal("production", options.PushServerEnvironment);
    }

    [Fact]
    public void EachValueResolvesIndependently()
    {
        var options = Resolve(
            new() { ["Loop:ApnsKey"] = "section-key", ["LOOP_APNS_KEY_ID"] = "LEGACYKEYID" }
        );

        Assert.Equal("section-key", options.ApnsKey);
        Assert.Equal("LEGACYKEYID", options.ApnsKeyId);
    }

    [Fact]
    public void NeitherSet_LeavesDefaults()
    {
        var options = Resolve(new());

        Assert.Null(options.ApnsKey);
        Assert.Null(options.ApnsKeyId);
        Assert.Null(options.DeveloperTeamId);
        Assert.Equal("production", options.PushServerEnvironment);
    }
}

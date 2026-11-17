using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol.Server;
using Moq;
using Nocturne.Core.Models;
using Nocturne.Tools.McpServer.Services;
using Nocturne.Tools.McpServer.Tools;
using Xunit;

namespace Nocturne.Tools.McpServer.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public class ToolBaseCollection
{
    public const string Name = "ToolBase";
}

[Collection(ToolBaseCollection.Name)]
public class ToolWindowTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 14, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(Now);
    private readonly List<string> _endpoints = [];

    public ToolWindowTests()
    {
        var api = new Mock<IApiService>();
        api.Setup(a => a.GetAsync(It.IsAny<string>()))
            .Callback<string>(_endpoints.Add)
            .ReturnsAsync("{}");
        api.Setup(a => a.GetAsync<Entry[]>(It.IsAny<string>()))
            .Callback<string>(_endpoints.Add)
            .ReturnsAsync([]);
        var services = new ServiceCollection().AddSingleton(api.Object).BuildServiceProvider();
        ToolBase.Initialize(services);
        EntryTools.Initialize(services);
    }

    private string OnlyEndpoint() => Uri.UnescapeDataString(_endpoints.Should().ContainSingle().Subject);

    [Fact]
    public async Task RecentGlucose_DefaultsToTheSixHoursBeforeNow()
    {
        await GlucoseTools.GetRecentGlucose(_clock);

        OnlyEndpoint().Should().Contain("from=2026-03-14T06:00:00.000Z");
    }

    [Fact]
    public async Task GlucoseStatistics_DefaultsToTheTwentyFourHoursBeforeNow()
    {
        await GlucoseTools.GetGlucoseStatistics(_clock);

        OnlyEndpoint().Should().Contain("from=2026-03-13T12:00:00.000Z");
    }

    [Fact]
    public async Task Window_FollowsTheClock()
    {
        await GlucoseTools.GetRecentGlucose(_clock);
        _clock.Advance(TimeSpan.FromHours(2));
        await GlucoseTools.GetRecentGlucose(_clock);

        _endpoints.Select(Uri.UnescapeDataString).Should().Satisfy(
            first => first.Contains("from=2026-03-14T06:00:00.000Z"),
            second => second.Contains("from=2026-03-14T08:00:00.000Z"));
    }

    [Fact]
    public async Task DashboardData_EndsAtNowAndStartsSixHoursEarlier()
    {
        await ChartDataTools.GetDashboardData(_clock);

        var endpoint = OnlyEndpoint();
        endpoint.Should().Contain($"startTime={Now.AddHours(-6).ToUnixTimeMilliseconds()}");
        endpoint.Should().Contain($"endTime={Now.ToUnixTimeMilliseconds()}");
    }

    [Fact]
    public async Task DashboardData_CapsTheWindowAtSeventyTwoHours()
    {
        await ChartDataTools.GetDashboardData(_clock, hours: 500);

        OnlyEndpoint().Should().Contain($"startTime={Now.AddHours(-72).ToUnixTimeMilliseconds()}");
    }

    [Fact]
    public async Task LegacyEntryCount_DefaultsToTheTwentyFourHoursBeforeNow()
    {
        await EntryTools.GetEntryCount(_clock);

        OnlyEndpoint().Should().Contain("dateString][$gte]=2026-03-13T12:00:00.000Z");
    }

    [Fact]
    public void TimeProvider_IsNotOfferedToTheModelAsAnArgument()
    {
        var services = new ServiceCollection().AddSingleton<TimeProvider>(_clock).BuildServiceProvider();
        var method = typeof(GlucoseTools).GetMethod(nameof(GlucoseTools.GetRecentGlucose))!;

        var tool = McpServerTool.Create(method, target: null, new McpServerToolCreateOptions { Services = services });

        tool.ProtocolTool.InputSchema.GetRawText().Should().Contain("hours").And.NotContain("timeProvider");
    }
}

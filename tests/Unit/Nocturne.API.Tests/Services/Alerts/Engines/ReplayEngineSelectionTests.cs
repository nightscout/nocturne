using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Alerts;
using Nocturne.API.Services.Alerts.Engines;
using Nocturne.API.Services.Alerts.Evaluators;
using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Repositories;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Engines;

/// <summary>The <c>Alerts:Engine</c> flag selects the replay engine as it selects the evaluation engine.</summary>
public class ReplayEngineSelectionTests
{
    private static ServiceProvider BuildProvider(string? engineFlag)
    {
        var configValues = new Dictionary<string, string?>();
        if (engineFlag is not null) configValues["Alerts:Engine"] = engineFlag;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(Mock.Of<IConditionTimerStore>());
        services.AddSingleton(Mock.Of<IAlertTrackerRepository>());
        services.AddSingleton<AlertRuleEvaluationGate>();
        services.AddAlertEvaluators();
        services.AddScoped<ConditionEvaluatorRegistry>();
        services.AddAlertEvaluationEngine(configuration, () => NativeProbeResult.Available);
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData(null, typeof(ManagedAlertReplayEngine))]
    [InlineData("rust", typeof(RustAlertReplayEngine))]
    [InlineData("shadow", typeof(ShadowAlertReplayEngine))]
    public void The_flag_selects_the_replay_engine(string? engineFlag, Type expected)
    {
        using var provider = BuildProvider(engineFlag);

        provider.GetRequiredService<IAlertReplayEngine>().Should().BeOfType(expected);
    }
}

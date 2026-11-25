using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Multitenancy;
using Nocturne.API.Services.Alerts;

namespace Nocturne.API.Tests.TestDoubles;

/// <summary>Instances of <see cref="AlertRuleRearm"/> for tests that do not run the evaluation a save starts.</summary>
internal static class Rearms
{
    /// <summary>A rearm whose saves start no evaluation: it reads no tenant, so it announces none.</summary>
    public static AlertRuleRearm Unannounced(AlertRuleEvaluationGate? gate = null) =>
        new(
            gate ?? new AlertRuleEvaluationGate(),
            new AlertFactEvaluationScheduler(
                Mock.Of<IServiceScopeFactory>(), TimeProvider.System, NullLogger<AlertFactEvaluationScheduler>.Instance),
            new HttpContextTenantAccessor());
}

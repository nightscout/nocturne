using Nocturne.API.Services.Alerts;
using Nocturne.Core.Alerts.Native;
using Nocturne.Core.Models.Alerts;

namespace Nocturne.API.Tests.Controllers.V4.Monitoring;

/// <summary>An <see cref="IAlertRuleConditionValidator"/> that accepts every rule.</summary>
internal sealed class NoConditionIssues : IAlertRuleConditionValidator
{
    public IReadOnlyList<RustValidationIssue> Validate(
        AlertConditionType conditionType,
        string conditionParamsJson,
        bool autoResolveEnabled,
        string? autoResolveParamsJson,
        string? clientConfigurationJson) => [];

    public ConditionUpdateCheck ValidateUpdate(
        AlertConditionType conditionType,
        string conditionParamsJson,
        bool autoResolveEnabled,
        string? autoResolveParamsJson,
        string? clientConfigurationJson,
        StoredConditionTrees stored) =>
        new([], conditionParamsJson, autoResolveParamsJson, clientConfigurationJson, []);
}

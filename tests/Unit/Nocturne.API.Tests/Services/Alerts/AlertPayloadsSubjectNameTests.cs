using FluentAssertions;
using Nocturne.API.Services.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

public class AlertPayloadsSubjectNameTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc);

    private static readonly AlertRuleSnapshot Rule = new(Guid.NewGuid(), Tenant, "low", AlertConditionType.Threshold,
        """{"schema_version":2,"operator":"<","value":70}""", AlertRuleSeverity.Warning, "{}", 0,
        AutoResolveEnabled: false, AutoResolveParams: null);

    private static readonly SensorContext Sensor = new()
    {
        LatestValue = 60m, LatestTimestamp = Now, TrendRate = null, LastReadingAt = Now,
    };

    private static TenantAlertContext Context(string? subjectName, string? displayName) =>
        new(Tenant, subjectName, "slug", displayName, true, null);

    private static AlertPayload Build(TenantAlertContext? tenant) =>
        AlertPayloads.Build(Rule, Sensor, Tenant, Guid.NewGuid(), Guid.NewGuid(), tenant, 1, Now);

    private static AlertPayload Unevaluable(TenantAlertContext? tenant) =>
        AlertPayloads.Unevaluable(Rule.Name, Rule.ConditionType, Rule.Severity, Tenant, Guid.NewGuid(), Guid.NewGuid(), tenant, 1, Now);

    public static TheoryData<string?, string?, string> Names => new()
    {
        { "Alex", "Slug Family", "Alex" },
        { null, "Slug Family", "Slug Family" },
        { null, null, "Unknown" },
    };

    [Theory]
    [MemberData(nameof(Names))]
    public void Build_NamesThePreferredNameThenTheDisplayName(string? subjectName, string? displayName, string expected) =>
        Build(Context(subjectName, displayName)).SubjectName.Should().Be(expected);

    [Theory]
    [MemberData(nameof(Names))]
    public void Unevaluable_NamesThePreferredNameThenTheDisplayName(string? subjectName, string? displayName, string expected) =>
        Unevaluable(Context(subjectName, displayName)).SubjectName.Should().Be(expected);

    [Fact]
    public void ANullContext_NamesUnknown()
    {
        Build(null).SubjectName.Should().Be("Unknown");
        Unevaluable(null).SubjectName.Should().Be("Unknown");
    }
}

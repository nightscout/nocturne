using System.Reflection;
using FluentAssertions;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.Core.Models.Tests.Authorization;

/// <summary>
/// <see cref="Scope.IsReadScope"/> decides the 401-vs-403 branch for an unauthenticated caller in
/// <c>RequireScopeAttribute</c>, which previously asked whether the string ended in <c>.read</c>.
/// The two must agree over the whole vocabulary, or a scope changes which status a share link gets.
/// </summary>
[Trait("Category", "Unit")]
public class ScopeReadTierTests
{
    /// <summary>Every scope string the vocabulary declares, read off the constants.</summary>
    public static TheoryData<string> DeclaredScopes()
    {
        var data = new TheoryData<string>();
        foreach (var scope in AllDeclaredScopes())
        {
            data.Add(scope);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(DeclaredScopes))]
    public void Classification_matches_the_suffix_rule_it_replaced(string scope)
    {
        Scope.IsReadScope(scope).Should().Be(
            scope.EndsWith(".read", StringComparison.Ordinal),
            $"'{scope}' must land on the same side of the read/write line as before");
    }

    [Fact]
    public void The_vocabulary_is_actually_enumerated()
    {
        AllDeclaredScopes().Should().HaveCountGreaterThan(20,
            "a reflection sweep that finds nothing would make the conformance check vacuous");
        AllDeclaredScopes().Where(Scope.IsReadScope).Should().HaveCount(13);
    }

    /// <summary>
    /// An atom that reads like a read and is not one. Asking the string's shape admitted it; asking
    /// the vocabulary does not.
    /// </summary>
    [Fact]
    public void A_scope_outside_the_vocabulary_is_not_a_read()
    {
        Scope.IsReadScope("members.read").Should().BeFalse();
        Scope.IsReadScope(Scope.AuditManage).Should().BeFalse();
        Scope.IsReadScope(Scope.FullAccess).Should().BeFalse();
    }

    /// <summary>
    /// The Viewer role holds <see cref="Scope.DeviceNotify"/>, which acknowledges an alert. That
    /// changes no record, treatment setting or access, which is all this predicate claims.
    /// </summary>
    [Fact]
    public void A_viewer_seed_role_views_without_changing_records_or_access()
    {
        Scope.IsViewOnlyForRecordsAndAccess(RoleSeeds.Permissions[RoleSeeds.Viewer]).Should().BeTrue();
    }

    [Theory]
    [InlineData(Scope.TreatmentsReadWrite)]
    [InlineData(Scope.TherapyReadWrite)]
    [InlineData(Scope.AlertsReadWrite)]
    [InlineData(Scope.TenantSettings)]
    [InlineData(Scope.MembersInvite)]
    [InlineData(Scope.AuditManage)]
    [InlineData(Scope.FullAccess)]
    public void A_grant_that_changes_records_settings_or_access_is_not_view_only(string authority)
    {
        Scope.IsViewOnlyForRecordsAndAccess([Scope.GlucoseRead, authority]).Should().BeFalse();
    }

    [Fact]
    public void A_grant_with_nothing_to_view_is_not_view_only()
    {
        Scope.IsViewOnlyForRecordsAndAccess([]).Should().BeFalse();
        Scope.IsViewOnlyForRecordsAndAccess([Scope.DeviceNotify, Scope.DeviceActuate]).Should().BeFalse();
    }

    private static List<string> AllDeclaredScopes() =>
        typeof(Scope)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();
}

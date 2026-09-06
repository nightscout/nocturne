using FluentAssertions;
using Nocturne.API.Authorization;
using Nocturne.API.Controllers.V1;
using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// The V1 routes that take the collection as a parameter cannot be gated by an attribute: a scope
/// list on the action or class is an OR across every collection the route serves, so a caller
/// holding one category reaches all of them. These pin the per-request resolution that replaces it.
/// </summary>
public class LegacyStorageReadScopeTests
{
    [Theory]
    [InlineData("entries", Scope.GlucoseRead)]
    [InlineData("treatments", Scope.TreatmentsRead)]
    [InlineData("devicestatus", Scope.DevicesRead)]
    [InlineData("profile", Scope.TherapyRead)]
    [InlineData("food", Scope.FoodRead)]
    public void EachStorage_MapsToItsCategory(string storage, string expected)
    {
        LegacyStorageReadScopes.RequiredReadScope(storage).Should().Be(expected);
    }

    [Theory]
    [InlineData("Entries")]
    [InlineData("TREATMENTS")]
    public void StorageMatching_IsCaseInsensitive(string storage)
    {
        // The services dispatch on storage.ToLowerInvariant(), so a differently-cased selector
        // reaches the same collection and must resolve to the same scope.
        LegacyStorageReadScopes.RequiredReadScope(storage).Should().NotBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("activity")]
    [InlineData("sensor_glucose")]
    public void AnUnclassifiedStorage_IsNotReadable(string? storage)
    {
        // activity included deliberately: it merges four categories, so it has no single governing
        // scope and must not resolve to one here.
        LegacyStorageReadScopes.RequiredReadScope(storage).Should().BeNull();
        LegacyStorageReadScopes.CanRead(Granted(Scope.FullAccess), storage).Should().BeFalse();
    }

    [Fact]
    public void OneCategorysGrant_CannotReadAnother()
    {
        // The hole this replaces: a class-level OR of glucose|treatments|devices admitted a
        // glucose-only grant to /api/v1/slice/treatments/... and every treatment record with it.
        var glucoseOnly = Granted(Scope.GlucoseRead);

        LegacyStorageReadScopes.CanRead(glucoseOnly, "entries").Should().BeTrue();
        LegacyStorageReadScopes.CanRead(glucoseOnly, "treatments").Should().BeFalse();
        LegacyStorageReadScopes.CanRead(glucoseOnly, "devicestatus").Should().BeFalse();
        LegacyStorageReadScopes.CanRead(glucoseOnly, "profile").Should().BeFalse();
        LegacyStorageReadScopes.CanRead(glucoseOnly, "food").Should().BeFalse();
    }

    [Fact]
    public void ReadWrite_SatisfiesTheReadRequirement()
    {
        LegacyStorageReadScopes
            .CanRead(Granted(Scope.TreatmentsReadWrite), "treatments")
            .Should().BeTrue();
    }

    [Fact]
    public void FullAccess_ReadsEveryClassifiedStorage()
    {
        var superuser = Granted(Scope.FullAccess);

        foreach (var storage in new[] { "entries", "treatments", "devicestatus", "profile", "food" })
        {
            LegacyStorageReadScopes.CanRead(superuser, storage).Should().BeTrue(storage);
        }
    }

    [Fact]
    public void EveryProductionGrantShape_KeepsItsSliceReads()
    {
        // The shape every active direct grant in production carries. It holds no alerts scope and
        // not always sleep, so the categories it does hold must keep working.
        var productionGrant = Granted(
            Scope.GlucoseReadWrite, Scope.TreatmentsReadWrite, Scope.DevicesReadWrite,
            Scope.TherapyReadWrite, Scope.HeartRateReadWrite, Scope.StepCountReadWrite,
            Scope.FoodReadWrite);

        foreach (var storage in new[] { "entries", "treatments", "devicestatus", "profile", "food" })
        {
            LegacyStorageReadScopes.CanRead(productionGrant, storage).Should().BeTrue(storage);
        }
    }

    [Fact]
    public void CountableStorage_IsFullyClassified()
    {
        // An accepted selector this does not classify is refused outright, so the count route would
        // answer 400 for a collection it dispatches on rather than counting it.
        foreach (var storage in CountController.CountableStorage)
        {
            var classified = LegacyStorageReadScopes.RequiredReadScope(storage) is not null
                || string.Equals(storage, "activity", StringComparison.OrdinalIgnoreCase);

            classified.Should().BeTrue(storage);
        }
    }

    [Fact]
    public void SliceableStorage_IsFullyClassified()
    {
        foreach (var storage in TimeQueryController.SliceableStorage)
        {
            LegacyStorageReadScopes.RequiredReadScope(storage).Should().NotBeNull(storage);
        }
    }

    private static IReadOnlySet<string> Granted(params string[] scopes) =>
        Scope.Normalize(scopes);
}

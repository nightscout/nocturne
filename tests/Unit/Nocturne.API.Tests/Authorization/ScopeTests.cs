using Nocturne.Core.Models.Authorization;
using Xunit;

namespace Nocturne.API.Tests.Authorization;

public class ScopeTests
{
    [Theory]
    [InlineData("glucose.read", true)]
    [InlineData("glucose.readwrite", true)]
    [InlineData("treatments.read", true)]
    [InlineData("treatments.readwrite", true)]
    [InlineData("devices.read", true)]
    [InlineData("devices.readwrite", true)]
    [InlineData("therapy.read", true)]
    [InlineData("therapy.readwrite", true)]
    [InlineData("alerts.read", true)]
    [InlineData("alerts.readwrite", true)]
    [InlineData("reports.read", true)]
    [InlineData("identity.read", true)]
    [InlineData("sharing.readwrite", true)]
    [InlineData("heartrate.read", true)]
    [InlineData("heartrate.readwrite", true)]
    [InlineData("stepcount.read", true)]
    [InlineData("stepcount.readwrite", true)]
    [InlineData("sleep.read", true)]
    [InlineData("sleep.readwrite", true)]
    [InlineData("food.read", true)]
    [InlineData("food.readwrite", true)]
    [InlineData("health.readwrite", true)]
    [InlineData("*", true)]
    [InlineData("health.read", true)]
    [InlineData("invalid.scope", false)]
    [InlineData("glucose.delete", false)]
    [InlineData("", false)]
    public void IsValid_ReturnsExpected(string scope, bool expected)
    {
        Assert.Equal(expected, Scope.IsValid(scope));
    }

    [Fact]
    public void Normalize_FullAccess_ExpandsToAllScopes()
    {
        var result = Scope.Normalize(new[] { "*" });

        Assert.Contains(Scope.FullAccess, result);
        Assert.Contains(Scope.GlucoseRead, result);
        Assert.Contains(Scope.TreatmentsReadWrite, result);
        Assert.Contains(Scope.TherapyRead, result);
        Assert.Contains(Scope.DevicesRead, result);
    }

    [Fact]
    public void Normalize_HealthRead_ExpandsToHealthScopes()
    {
        var result = Scope.Normalize(new[] { "health.read" });

        Assert.Contains(Scope.GlucoseRead, result);
        Assert.Contains(Scope.TreatmentsRead, result);
        Assert.Contains(Scope.DevicesRead, result);
        Assert.Contains(Scope.TherapyRead, result);
        Assert.DoesNotContain(Scope.AlertsRead, result);
        Assert.DoesNotContain(Scope.FullAccess, result);
    }

    [Fact]
    public void Normalize_InvalidScopesAreIgnored()
    {
        var result = Scope.Normalize(new[] { "glucose.read", "invalid.scope" });

        Assert.Contains(Scope.GlucoseRead, result);
        Assert.Single(result);
    }

    [Fact]
    public void SatisfiesScope_ExactMatch()
    {
        var granted = new HashSet<string> { "glucose.read" };
        Assert.True(Scope.Satisfies(granted, "glucose.read"));
    }

    [Fact]
    public void SatisfiesScope_FullAccessSatisfiesEverything()
    {
        var granted = new HashSet<string> { "*" };

        Assert.True(Scope.Satisfies(granted, "glucose.read"));
        Assert.True(Scope.Satisfies(granted, "treatments.readwrite"));
        Assert.True(Scope.Satisfies(granted, "therapy.read"));
        Assert.True(Scope.Satisfies(granted, "*"));
    }

    [Fact]
    public void SatisfiesScope_ReadWriteImpliesRead()
    {
        var granted = new HashSet<string> { "glucose.readwrite" };

        Assert.True(Scope.Satisfies(granted, "glucose.read"));
        Assert.True(Scope.Satisfies(granted, "glucose.readwrite"));
        Assert.False(Scope.Satisfies(granted, "treatments.read"));
    }

    [Fact]
    public void SatisfiesScope_ReadDoesNotImplyReadWrite()
    {
        var granted = new HashSet<string> { "glucose.read" };

        Assert.True(Scope.Satisfies(granted, "glucose.read"));
        Assert.False(Scope.Satisfies(granted, "glucose.readwrite"));
    }

    [Fact]
    public void SatisfiesScope_NoScopesSatisfiesNothing()
    {
        var granted = new HashSet<string>();

        Assert.False(Scope.Satisfies(granted, "glucose.read"));
        Assert.False(Scope.Satisfies(granted, "*"));
    }

    [Fact]
    public void Normalize_HealthRead_IncludesHeartRateAndStepCount()
    {
        var result = Scope.Normalize(new[] { "health.read" });

        Assert.Contains(Scope.HeartRateRead, result);
        Assert.Contains(Scope.StepCountRead, result);
        Assert.Contains(Scope.SleepRead, result);
    }

    [Fact]
    public void Normalize_HealthReadWrite_ExpandsToAllHealthWriteScopes()
    {
        var result = Scope.Normalize(new[] { "health.readwrite" });

        Assert.Contains(Scope.GlucoseReadWrite, result);
        Assert.Contains(Scope.TreatmentsReadWrite, result);
        Assert.Contains(Scope.DevicesReadWrite, result);
        Assert.Contains(Scope.TherapyReadWrite, result);
        Assert.Contains(Scope.HeartRateReadWrite, result);
        Assert.Contains(Scope.StepCountReadWrite, result);
        Assert.Contains(Scope.SleepReadWrite, result);
        Assert.DoesNotContain(Scope.AlertsReadWrite, result);
    }

    [Fact]
    public void SatisfiesScope_HeartRateReadWriteImpliesRead()
    {
        var granted = new HashSet<string> { "heartrate.readwrite" };

        Assert.True(Scope.Satisfies(granted, "heartrate.read"));
        Assert.True(Scope.Satisfies(granted, "heartrate.readwrite"));
        Assert.False(Scope.Satisfies(granted, "glucose.read"));
    }

    [Fact]
    public void SatisfiesScope_StepCountReadWriteImpliesRead()
    {
        var granted = new HashSet<string> { "stepcount.readwrite" };

        Assert.True(Scope.Satisfies(granted, "stepcount.read"));
        Assert.True(Scope.Satisfies(granted, "stepcount.readwrite"));
        Assert.False(Scope.Satisfies(granted, "glucose.read"));
    }

    [Fact]
    public void SatisfiesScope_SleepReadWriteImpliesRead()
    {
        var granted = new HashSet<string> { "sleep.readwrite" };

        Assert.True(Scope.Satisfies(granted, "sleep.read"));
        Assert.True(Scope.Satisfies(granted, "sleep.readwrite"));
        Assert.False(Scope.Satisfies(granted, "glucose.read"));
    }

    [Fact]
    public void Normalize_HealthRead_IncludesFood()
    {
        var result = Scope.Normalize(new[] { "health.read" });

        Assert.Contains(Scope.FoodRead, result);
    }

    [Fact]
    public void Normalize_HealthReadWrite_IncludesFood()
    {
        var result = Scope.Normalize(new[] { "health.readwrite" });

        Assert.Contains(Scope.FoodReadWrite, result);
    }

    [Fact]
    public void SatisfiesScope_FoodReadWriteImpliesRead()
    {
        var granted = new HashSet<string> { "food.readwrite" };

        Assert.True(Scope.Satisfies(granted, "food.read"));
        Assert.True(Scope.Satisfies(granted, "food.readwrite"));
        Assert.False(Scope.Satisfies(granted, "entries.read"));
    }
}

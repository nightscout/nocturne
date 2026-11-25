using System.Text.RegularExpressions;
using FluentAssertions;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.Core.Models.Tests.Alerts;

public class AlertRuleGlucoseFreshnessTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "nocturne.sln")) && !File.Exists(Path.Combine(dir.FullName, "nocturne.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("The repository root was not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void The_default_is_the_engines_default()
    {
        var path = Path.Combine(RepoRoot(), "crates", "nocturne-alerts-core", "src", "eval", "glucose.rs");
        File.Exists(path).Should().BeTrue($"the engine source {path} holds the default bound");

        var match = Regex.Match(
            File.ReadAllText(path),
            @"pub const DEFAULT_GLUCOSE_FRESHNESS:\s*TimeDelta\s*=\s*TimeDelta::minutes\((\d+)\)");
        match.Success.Should().BeTrue("glucose.rs declares DEFAULT_GLUCOSE_FRESHNESS in whole minutes");

        AlertRuleGlucoseFreshness.DefaultMinutes.Should().Be(int.Parse(match.Groups[1].Value));
    }

    [Fact]
    public void The_maximum_is_the_engines_maximum()
    {
        var path = Path.Combine(RepoRoot(), "crates", "nocturne-alerts-core", "src", "eval", "glucose.rs");
        File.Exists(path).Should().BeTrue($"the engine source {path} holds the maximum bound");

        var match = Regex.Match(File.ReadAllText(path), @"pub const MAX_GLUCOSE_FRESHNESS_MINUTES:\s*i64\s*=\s*(\d+)");
        match.Success.Should().BeTrue("glucose.rs declares MAX_GLUCOSE_FRESHNESS_MINUTES");

        AlertRuleGlucoseFreshness.MaxMinutes.Should().Be(int.Parse(match.Groups[1].Value));
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Nocturne.Aspire.ServiceDefaults.Tests.Deployment;

/// <summary>
/// Every <c>httpGet</c> path in the Helm chart must name a route the service actually serves.
/// A probe on an unserved path never succeeds: the pod stays NotReady and the Deployment never
/// becomes Available, so a rollout wait never converges even though the chart installed cleanly.
/// </summary>
public class HelmProbePathCoverageTests
{
    private static readonly IReadOnlyList<string> MappedPaths = MapDefaultEndpointPaths();

    private static readonly IReadOnlyList<(string File, int Line, string Path)> ProbePaths =
        ReadHttpGetPaths();

    [Fact]
    public void EveryChartProbePathIsMappedByServiceDefaults()
    {
        var unserved = ProbePaths
            .Where(probe => !MappedPaths.Contains(probe.Path, StringComparer.OrdinalIgnoreCase))
            .Select(probe => $"{probe.File}:{probe.Line} {probe.Path}")
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToList();

        unserved.Should().BeEmpty(
            "a probe on a path nothing answers can only ever fail, so the pod it guards never "
            + "becomes Ready. Mapped: " + string.Join(", ", MappedPaths) + ". Unserved:\n  "
            + string.Join("\n  ", unserved));
    }

    /// <summary>
    /// Non-vacuity: both halves of the comparison are derived, so either returning nothing would
    /// pass the guard above while proving nothing.
    /// </summary>
    [Fact]
    public void BothSidesOfTheComparisonAreDiscovered()
    {
        MappedPaths.Should().BeEquivalentTo(["/health", "/alive"],
            "the probe paths are checked against what MapDefaultEndpoints maps, so a change there "
            + "is a chart change too");

        ProbePaths.Select(p => p.Path).Should().Contain("/alive",
            "the API and demo deployments both probe liveness over HTTP, so finding no such path "
            + "means the template scan has stopped working");
    }

    private static IReadOnlyList<string> MapDefaultEndpointPaths()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.AddDefaultHealthChecks();
        var app = builder.Build();
        app.MapDefaultEndpoints();

        return ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => "/" + endpoint.RoutePattern.RawText!.TrimStart('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<(string File, int Line, string Path)> ReadHttpGetPaths()
    {
        var found = new List<(string, int, string)>();

        foreach (var file in Directory.EnumerateFiles(TemplateDirectory(), "*.y*ml", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Trim() != "httpGet:")
                {
                    continue;
                }

                var indent = IndentOf(lines[i]);
                for (var j = i + 1; j < lines.Length && IndentOf(lines[j]) > indent; j++)
                {
                    var key = lines[j].Trim();
                    if (key.StartsWith("path:", StringComparison.Ordinal))
                    {
                        found.Add((
                            Path.GetFileName(file),
                            j + 1,
                            key["path:".Length..].Trim().Trim('"', '\'')));
                    }
                }
            }
        }

        return found;
    }

    private static int IndentOf(string line) =>
        line.Trim().Length == 0 ? int.MaxValue : line.Length - line.TrimStart().Length;

    private static string TemplateDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "nocturne.sln")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(
            dir?.FullName ?? throw new InvalidOperationException(
                "Could not locate the repo root (nocturne.sln) above " + AppContext.BaseDirectory),
            "deploy", "helm", "nocturne", "templates");
    }
}

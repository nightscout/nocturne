using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nocturne.Alerts.ParityCorpus.Generator.Harness;

/// <summary>
/// Locates and loads the committed golden corpus (<c>tests/Parity/AlertEngineCorpus</c>)
/// by walking up from the build output directory to the repo root (<c>nocturne.sln</c>).
/// Shared by the generator and every parity test project.
/// </summary>
public static class CorpusLocator
{
    public static string CorpusDirectory() =>
        Path.Combine(FindRepoRoot(), "tests", "Parity", "AlertEngineCorpus");

    /// <summary>Scenario names (files without the <c>.expected.json</c> suffix), ordinal-sorted.</summary>
    public static IReadOnlyList<string> EnumerateScenarioNames() =>
        Directory.EnumerateFiles(CorpusDirectory(), "*.json")
            .Where(p => !p.EndsWith(".expected.json", StringComparison.Ordinal))
            .Select(p => Path.GetFileNameWithoutExtension(p)!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    /// <summary>Loads one scenario plus its committed expected snapshot.</summary>
    public static async Task<(ScenarioFile Scenario, JsonNode Expected)> LoadScenarioAsync(string name)
    {
        var dir = CorpusDirectory();
        var scenarioJson = await File.ReadAllTextAsync(Path.Combine(dir, $"{name}.json"));
        var expectedJson = await File.ReadAllTextAsync(Path.Combine(dir, $"{name}.expected.json"));
        var scenario = JsonSerializer.Deserialize<ScenarioFile>(scenarioJson, CorpusJson.Options)
            ?? throw new InvalidOperationException($"Failed to parse scenario '{name}'");
        return (scenario, JsonNode.Parse(expectedJson)!);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "nocturne.sln")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName
            ?? throw new InvalidOperationException(
                "Could not locate the repo root (nocturne.sln) above " + AppContext.BaseDirectory);
    }
}

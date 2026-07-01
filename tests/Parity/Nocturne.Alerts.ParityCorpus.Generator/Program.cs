using System.Text;
using System.Text.Json;
using Nocturne.Alerts.ParityCorpus.Generator.Harness;
using Nocturne.Alerts.ParityCorpus.Generator.Scenarios;

// Corpus generator: runs every scenario through the live C# alert engine and writes
// <name>.json (scenario) + <name>.expected.json (snapshot) into the corpus directory.
//
// Usage:
//   dotnet run --project tests/Parity/Nocturne.Alerts.ParityCorpus.Generator            # (re)generate
//   dotnet run --project tests/Parity/Nocturne.Alerts.ParityCorpus.Generator -- --check # verify committed corpus is current
//
// --check regenerates in memory and fails (exit 1) if any file differs from the
// committed corpus — CI uses it to keep the corpus honest while the C# engine is still
// the authority.

var check = args.Contains("--check");
var corpusDir = args.FirstOrDefault(a => !a.StartsWith("--")) ?? CorpusLocator.CorpusDirectory();

var scenarios = new List<ScenarioFile>();
scenarios.AddRange(GlucoseLeafScenarios.All());
scenarios.AddRange(ComparisonLeafScenarios.All());
scenarios.AddRange(TimeScenarios.All());
scenarios.AddRange(LoopDeviceScenarios.All());
scenarios.AddRange(AlertStateAndTimeSinceScenarios.All());
scenarios.AddRange(ContainerScenarios.All());
scenarios.AddRange(SustainedScenarios.All());
scenarios.AddRange(TrackerScenarios.All());
scenarios.AddRange(AutoResolveScenarios.All());

var duplicate = scenarios.GroupBy(s => s.Name).FirstOrDefault(g => g.Count() > 1);
if (duplicate is not null)
{
    Console.Error.WriteLine($"Duplicate scenario name: {duplicate.Key}");
    return 1;
}

Directory.CreateDirectory(corpusDir);

var runner = new ScenarioRunner();
var failures = 0;
var written = 0;

foreach (var scenario in scenarios.OrderBy(s => s.Name, StringComparer.Ordinal))
{
    ExpectedFile expected;
    try
    {
        expected = await runner.RunAsync(scenario, CancellationToken.None);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"FAIL {scenario.Name}: {ex.Message}");
        failures++;
        continue;
    }

    var scenarioPath = Path.Combine(corpusDir, $"{scenario.Name}.json");
    var expectedPath = Path.Combine(corpusDir, $"{scenario.Name}.expected.json");
    var scenarioJson = JsonSerializer.Serialize(scenario, CorpusJson.Options) + "\n";
    var expectedJson = JsonSerializer.Serialize(expected, CorpusJson.Options) + "\n";
    // Normalise to LF so output is identical across OSes (the corpus is committed).
    scenarioJson = scenarioJson.ReplaceLineEndings("\n");
    expectedJson = expectedJson.ReplaceLineEndings("\n");

    if (check)
    {
        failures += Verify(scenarioPath, scenarioJson);
        failures += Verify(expectedPath, expectedJson);
    }
    else
    {
        await File.WriteAllTextAsync(scenarioPath, scenarioJson, new UTF8Encoding(false));
        await File.WriteAllTextAsync(expectedPath, expectedJson, new UTF8Encoding(false));
        written += 2;
    }
}

if (check)
{
    // Stale files (scenario renamed/removed but file still committed) are failures too.
    var expectedNames = scenarios
        .SelectMany(s => new[] { $"{s.Name}.json", $"{s.Name}.expected.json" })
        .ToHashSet(StringComparer.Ordinal);
    foreach (var file in Directory.EnumerateFiles(corpusDir, "*.json"))
    {
        if (!expectedNames.Contains(Path.GetFileName(file)))
        {
            Console.Error.WriteLine($"STALE {Path.GetFileName(file)} (no matching scenario)");
            failures++;
        }
    }
}

Console.WriteLine(check
    ? $"Checked {scenarios.Count} scenarios against {corpusDir}: {(failures == 0 ? "up to date" : $"{failures} mismatches")}"
    : $"Wrote {written} files for {scenarios.Count} scenarios to {corpusDir}");

return failures == 0 ? 0 : 1;

static int Verify(string path, string expectedContent)
{
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"MISSING {Path.GetFileName(path)}");
        return 1;
    }
    var actual = File.ReadAllText(path).ReplaceLineEndings("\n");
    if (actual != expectedContent)
    {
        Console.Error.WriteLine($"DIFF {Path.GetFileName(path)}");
        return 1;
    }
    return 0;
}

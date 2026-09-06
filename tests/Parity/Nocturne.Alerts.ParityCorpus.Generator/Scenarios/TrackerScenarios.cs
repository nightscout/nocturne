using Nocturne.Alerts.ParityCorpus.Generator.Harness;
using static Nocturne.Alerts.ParityCorpus.Generator.Scenarios.B;

namespace Nocturne.Alerts.ParityCorpus.Generator.Scenarios;

/// <summary>
/// Excursion tracker state machine: confirmation counting, hysteresis enter/resume/expiry
/// (including the sliding UpdatedAt proxy), and auto-resolve interplay.
/// </summary>
public static class TrackerScenarios
{
    private const string Low70 = """{"direction": "below", "value": 70}""";

    public static IEnumerable<ScenarioFile> All()
    {
        yield return Scenario(
            "tracker-immediate-open",
            "ConfirmationReadings <= 1 opens an excursion on the first true evaluation; continues while true",
            [Rule(1, "threshold", Low70)],
            [
                Tick(T(0), Ctx(T(0), glucose: 100m)),   // idle, none
                Tick(T(5), Ctx(T(5), glucose: 65m)),    // opened
                Tick(T(10), Ctx(T(10), glucose: 64m)),  // continues
            ]);

        yield return Scenario(
            "tracker-confirmation-counting",
            "ConfirmationReadings=3: first true enters confirming(1); two more trues open; the open resets the count",
            [Rule(1, "threshold", Low70, confirm: 3)],
            [
                Tick(T(0), Ctx(T(0), glucose: 65m)),    // confirming, count 1
                Tick(T(5), Ctx(T(5), glucose: 65m)),    // confirming, count 2
                Tick(T(10), Ctx(T(10), glucose: 65m)),  // count 3 -> opened
            ]);

        yield return Scenario(
            "tracker-confirmation-reset-on-false",
            "a single false during confirming fully resets to idle (count back to 0)",
            [Rule(1, "threshold", Low70, confirm: 2)],
            [
                Tick(T(0), Ctx(T(0), glucose: 65m)),    // confirming, 1
                Tick(T(5), Ctx(T(5), glucose: 100m)),   // reset to idle
                Tick(T(10), Ctx(T(10), glucose: 65m)),  // confirming, 1 (starts over)
                Tick(T(15), Ctx(T(15), glucose: 65m)),  // opened
            ]);

        yield return Scenario(
            "tracker-hysteresis-resume",
            "active -> false enters hysteresis; true while in hysteresis resumes active without a new excursion",
            [Rule(1, "threshold", Low70, hysteresis: 30)],
            [
                Tick(T(0), Ctx(T(0), glucose: 65m)),    // opened (excursion 1)
                Tick(T(5), Ctx(T(5), glucose: 100m)),   // hysteresis_started
                Tick(T(10), Ctx(T(10), glucose: 65m)),  // hysteresis_resumed (same excursion 1)
                Tick(T(15), Ctx(T(15), glucose: 64m)),  // continues
            ]);

        yield return Scenario(
            "tracker-hysteresis-expiry-on-gap",
            "the per-evaluation expiry proxy is the previous tick's UpdatedAt: with HysteresisMinutes=10, 5-minute ticks never expire, but a 10-minute evaluation gap closes",
            [Rule(1, "threshold", Low70, hysteresis: 10)],
            [
                Tick(T(0), Ctx(T(0), glucose: 65m)),    // opened
                Tick(T(5), Ctx(T(5), glucose: 100m)),   // hysteresis_started
                Tick(T(10), Ctx(T(10), glucose: 100m)), // gap 5m < 10m: still hysteresis (none)
                Tick(T(15), Ctx(T(15), glucose: 100m)), // proxy slid to T10; gap 5m: still hysteresis
                Tick(T(25), Ctx(T(25), glucose: 100m)), // gap 10m >= 10m: closed (reason hysteresis)
            ]);

        yield return Scenario(
            "tracker-hysteresis-zero-minutes",
            "HysteresisMinutes=0 closes on the very next false evaluation after entering hysteresis",
            [Rule(1, "threshold", Low70, hysteresis: 0)],
            [
                Tick(T(0), Ctx(T(0), glucose: 65m)),    // opened
                Tick(T(5), Ctx(T(5), glucose: 100m)),   // hysteresis_started
                Tick(T(10), Ctx(T(10), glucose: 100m)), // closed
                Tick(T(15), Ctx(T(15), glucose: 65m)),  // re-opens (excursion 2)
            ]);

        yield return Scenario(
            "tracker-reopen-after-close-counts-fresh",
            "after a hysteresis close, a new excursion requires fresh confirmation",
            [Rule(1, "threshold", Low70, confirm: 2, hysteresis: 0)],
            [
                Tick(T(0), Ctx(T(0), glucose: 65m)),    // confirming 1
                Tick(T(5), Ctx(T(5), glucose: 65m)),    // opened (excursion 1)
                Tick(T(10), Ctx(T(10), glucose: 100m)), // hysteresis_started
                Tick(T(15), Ctx(T(15), glucose: 100m)), // closed
                Tick(T(20), Ctx(T(20), glucose: 65m)),  // confirming 1 (fresh count)
                Tick(T(25), Ctx(T(25), glucose: 65m)),  // opened (excursion 2)
            ]);
    }
}

using Nocturne.Alerts.ParityCorpus.Generator.Harness;
using static Nocturne.Alerts.ParityCorpus.Generator.Scenarios.B;

namespace Nocturne.Alerts.ParityCorpus.Generator.Scenarios;

/// <summary>
/// Excursion tracker state machine: confirmation counting, hysteresis enter/resume/expiry
/// measured from the hysteresis start, and auto-resolve interplay.
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
            "tracker-hysteresis-window-from-entry",
            "HysteresisMinutes=10 with 5-minute ticks: the window runs from the hysteresis start, so it closes 10 minutes after entry",
            [Rule(1, "threshold", Low70, hysteresis: 10)],
            [
                Tick(T(0), Ctx(T(0), glucose: 65m)),    // opened
                Tick(T(5), Ctx(T(5), glucose: 100m)),   // hysteresis_started at T5
                Tick(T(10), Ctx(T(10), glucose: 100m)), // 5m since entry: none
                Tick(T(14), Ctx(T(14), glucose: 100m)), // 9m since entry: none
                Tick(T(15), Ctx(T(15), glucose: 100m)), // 10m since entry: closed (reason hysteresis)
            ]);

        yield return Scenario(
            "tracker-hysteresis-thirty-minutes",
            "HysteresisMinutes=30 holds the excursion through six false 5-minute ticks and closes on the one 30 minutes after entry",
            [Rule(1, "threshold", Low70, hysteresis: 30)],
            [
                Tick(T(0), Ctx(T(0), glucose: 65m)),    // opened
                Tick(T(5), Ctx(T(5), glucose: 100m)),   // hysteresis_started at T5
                Tick(T(10), Ctx(T(10), glucose: 100m)),
                Tick(T(15), Ctx(T(15), glucose: 100m)),
                Tick(T(20), Ctx(T(20), glucose: 100m)),
                Tick(T(25), Ctx(T(25), glucose: 100m)),
                Tick(T(30), Ctx(T(30), glucose: 100m)), // 25m since entry: none
                Tick(T(35), Ctx(T(35), glucose: 100m)), // 30m since entry: closed
            ]);

        yield return Scenario(
            "tracker-hysteresis-reentry-restarts-window",
            "a true tick in hysteresis resumes the same excursion; the next false tick starts a fresh window",
            [Rule(1, "threshold", Low70, hysteresis: 15)],
            [
                Tick(T(0), Ctx(T(0), glucose: 65m)),    // opened (excursion 1)
                Tick(T(5), Ctx(T(5), glucose: 100m)),   // hysteresis_started at T5
                Tick(T(15), Ctx(T(15), glucose: 65m)),  // hysteresis_resumed (excursion 1)
                Tick(T(20), Ctx(T(20), glucose: 100m)), // hysteresis_started at T20
                Tick(T(30), Ctx(T(30), glucose: 100m)), // 25m after the first entry, 10m after this one: none
                Tick(T(35), Ctx(T(35), glucose: 100m)), // 15m since T20: closed
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

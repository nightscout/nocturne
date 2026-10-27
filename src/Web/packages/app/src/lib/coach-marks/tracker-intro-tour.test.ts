import { readFileSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { CoachMarkContext, type CoachMarkAdapter } from "@nocturne/coach";
import { coachCopy, coachLabels } from "./copy.svelte";
import { ONBOARDING_CORE_GATE, sequences } from "./sequences";

vi.mock("$app/paths", () => import("#lib/test-stubs/app-paths.js"));

const SRC = fileURLToPath(new URL("../..", import.meta.url));
const TOUR = "tracker-intro";
const copy: Record<string, { title: string; description: string }> =
  coachCopy();
const steps = sequences[TOUR]?.steps ?? [];

/**
 * Where each step's target is attached; the settings steps are on their own
 * pages.
 */
const TARGETS: Record<string, string> = {
  "tracker-intro.pill": join(
    "lib",
    "components",
    "status-pills",
    "TrackerPillBar.svelte"
  ),
  "tracker-intro.complete": join(
    "lib",
    "components",
    "status-pills",
    "TrackerPillBar.svelte"
  ),
  "tracker-intro.settings": join(
    "routes",
    "(authenticated)",
    "trackers",
    "+page.svelte"
  ),
  "tracker-intro.rail": join(
    "routes",
    "(authenticated)",
    "settings",
    "appearance",
    "+page.svelte"
  ),
};

/**
 * `sequences.test` proves every `coachFor` key sits in a `coachmark` call; this
 * places each one.
 */
const attachedIn = (file: string, key: string) =>
  readFileSync(join(SRC, file), "utf8").includes(`coachFor("${key}")`);

describe("tracker introduction tour", () => {
  it("is registered under the name the legacy conversion dialog starts", () => {
    const dialog = readFileSync(
      join(
        SRC,
        "lib",
        "components",
        "trackers",
        "CreateTrackersFromLegacyDialog.svelte"
      ),
      "utf8"
    );

    expect(dialog).toContain(`const TRACKER_INTRO = "${TOUR}";`);
    expect(sequences).toHaveProperty(TOUR);
  });

  it("tours the pill, its change control, the tracker settings and the rail settings in order", () => {
    expect(steps).toEqual(Object.keys(TARGETS));
  });

  it("runs only when started", () => {
    expect(sequences[TOUR].startOnly).toBe(true);
  });

  it.each(Object.entries(TARGETS))("attaches %s in %s", (key, file) => {
    expect(attachedIn(file, key)).toBe(true);
  });

  it("has copy for every step", () => {
    for (const key of steps) {
      expect(copy[key]?.title, key).toBeTruthy();
      expect(copy[key]?.description, key).toBeTruthy();
    }
  });

  it("has no copy that no step shows", () => {
    const unused = Object.keys(copy).filter(
      (key) => key.startsWith(`${TOUR}.`) && !steps.includes(key)
    );

    expect(unused).toEqual([]);
  });

  it("writes every sentence in 20 words or fewer", () => {
    for (const key of steps)
      for (const sentence of copy[key].description.split(/(?<=\.)\s+/))
        expect(
          sentence.split(/\s+/).length,
          `${key}: ${sentence}`
        ).toBeLessThanOrEqual(20);
  });

  it("tells the reader that Warning and Hazard alerts come from the thresholds", () => {
    expect(copy["tracker-intro.settings"].description).toContain(
      "Warning and Hazard alerts come from the notification thresholds."
    );
  });
});

describe("a run of the tracker introduction", () => {
  const SETTLE = 10;
  const settle = () => vi.advanceTimersByTime(SETTLE * 2);
  const [PILL, COMPLETE, SETTINGS, RAIL] = steps;
  const DISCOVERY = "feature-intro.calendar-views";

  /**
   * Registers each key on one page; the returned function navigates away from
   * it.
   */
  function visit(context: CoachMarkContext, ...keys: string[]): () => void {
    const unregister = keys.map((key) =>
      context.register({
        key,
        step: 0,
        title: key,
        description: key,
        priority: 0,
        element: {} as HTMLElement,
      })
    );
    return () => {
      context.clearQuiet();
      for (const leave of unregister) leave();
    };
  }

  async function started() {
    const adapter: CoachMarkAdapter = {
      fetchAll: async () => [],
      update: async () => {},
    };
    const context = new CoachMarkContext(
      adapter,
      coachLabels(),
      sequences,
      SETTLE,
      2000,
      () => ({
        [ONBOARDING_CORE_GATE]: true,
      })
    );
    await context.initialize();
    return context;
  }

  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it("is never chosen on its own, and its hotspots raise nothing", async () => {
    const context = await started();
    visit(context, PILL, COMPLETE, SETTINGS, RAIL);
    settle();

    expect(context.activeKey).toBeNull();
    expect(context.isMarkEligible(PILL)).toBe(false);
    context.activate(PILL, 0);
    expect(context.activeKey).toBeNull();
  });

  it("waits for each settings page once started, and ends after its last step", async () => {
    const context = await started();
    const leaveDashboard = visit(context, PILL, COMPLETE);
    context.startSequence(TOUR);

    expect(context.activeKey).toBe(PILL);
    context.complete(PILL);
    expect(context.activeKey).toBe(COMPLETE);
    context.complete(COMPLETE);
    expect(context.activeKey).toBeNull();

    leaveDashboard();
    const leaveTrackers = visit(context, SETTINGS);
    expect(context.activeKey).toBe(SETTINGS);
    context.complete(SETTINGS);

    leaveTrackers();
    visit(context, RAIL);
    expect(context.activeKey).toBe(RAIL);
    context.complete(RAIL);

    expect(context.activeKey).toBeNull();
    expect(context.isMarkEligible(PILL)).toBe(false);
  });

  it("ends on a dismissal, and no later page raises its steps", async () => {
    const context = await started();
    const leaveDashboard = visit(context, PILL, COMPLETE);
    context.startSequence(TOUR);
    context.dismiss(PILL);

    leaveDashboard();
    visit(context, SETTINGS);
    settle();

    expect(context.activeKey).toBeNull();
    expect(context.getStatus(SETTINGS)).toBe("dismissed");
  });

  it("shows a settings step when the pill never mounts", async () => {
    const context = await started();
    visit(context, SETTINGS);
    context.startSequence(TOUR);

    expect(context.activeKey).toBe(SETTINGS);
  });

  it("leaves other tours to run while it waits, and takes the floor once their mark is done", async () => {
    const context = await started();
    const leaveDashboard = visit(context, PILL, COMPLETE);
    context.startSequence(TOUR);
    context.complete(PILL);
    context.complete(COMPLETE);
    leaveDashboard();

    visit(context, DISCOVERY);
    settle();
    expect(context.activeKey).toBe(DISCOVERY);

    visit(context, SETTINGS);
    expect(context.activeKey).toBe(DISCOVERY);
    context.complete(DISCOVERY);
    expect(context.activeKey).toBe(SETTINGS);
  });

  it("outlives the dismissal of another tour's mark raised from its dot", async () => {
    const context = await started();
    const leaveDashboard = visit(context, PILL, COMPLETE);
    context.startSequence(TOUR);
    context.complete(PILL);
    context.complete(COMPLETE);
    leaveDashboard();

    const leaveCalendar = visit(context, DISCOVERY);
    context.activate(DISCOVERY, 0);
    context.dismiss(DISCOVERY);
    expect(context.getStatus(DISCOVERY)).toBe("dismissed");
    expect(context.getStatus(SETTINGS)).toBe("unseen");

    leaveCalendar();
    visit(context, SETTINGS);
    expect(context.activeKey).toBe(SETTINGS);
  });

  it("replays only the steps on the page, as any replay does", async () => {
    const context = await started();
    const leaveDashboard = visit(context, PILL, COMPLETE);
    context.replaySequence(TOUR);
    context.complete(PILL);
    context.complete(COMPLETE);

    leaveDashboard();
    visit(context, SETTINGS);
    settle();

    expect(context.activeKey).toBeNull();
  });
});

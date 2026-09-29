import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { CoachMarkContext, selectActiveMark } from "@nocturne/coach";
import type { CoachGates, CoachMarkAdapter, MarkRegistration, MarkState } from "@nocturne/coach";
import { ONBOARDING_CORE_GATE, sequences } from "./sequences";

const SETTLE = 10;

function makeRegistration(key: string): MarkRegistration {
  return { key, step: 0, title: key, description: key, priority: 0, element: {} as HTMLElement };
}

function stored(markKey: string, status: MarkState["status"]): MarkState {
  return { id: markKey, markKey, status, seenAt: null, completedAt: null };
}

async function started(gates: CoachGates, states: MarkState[] = []) {
  const adapter: CoachMarkAdapter = { fetchAll: async () => states, update: async () => {} };
  const context = new CoachMarkContext(adapter, sequences, SETTLE, 2000, () => gates);
  await context.initialize();
  return context;
}

const settle = () => vi.advanceTimersByTime(SETTLE * 2);

beforeEach(() => {
  vi.useFakeTimers();
});

afterEach(() => {
  vi.useRealTimers();
});

const DISCOVERY_STEP = sequences["feature-intro"].steps[0];

describe("the discovery tours", () => {
  it("wait for the onboarding core", async () => {
    const context = await started({ [ONBOARDING_CORE_GATE]: false });
    context.register(makeRegistration(DISCOVERY_STEP));
    settle();

    expect(context.isMarkEligible(DISCOVERY_STEP)).toBe(false);
    expect(context.activeKey).toBeNull();
  });

  it("appear once the core is complete", async () => {
    const context = await started({ [ONBOARDING_CORE_GATE]: true });
    context.register(makeRegistration(DISCOVERY_STEP));
    settle();

    expect(context.isMarkEligible(DISCOVERY_STEP)).toBe(true);
    expect(context.activeKey).toBe(DISCOVERY_STEP);
  });

  it("stay shut when the host sets no gate at all", async () => {
    const context = await started({});
    context.register(makeRegistration(DISCOVERY_STEP));
    settle();

    expect(context.activeKey).toBeNull();
  });
});

describe("a prerequisite that names nothing", () => {
  it("is unmet rather than waved through", () => {
    const result = selectActiveMark(
      new Map(),
      [makeRegistration("tour.one")],
      { tour: { priority: 1, steps: ["tour.one"], prerequisite: "retired-sequence" } },
      {},
    );
    expect(result).toBeNull();
  });
});

describe("the quick tour", () => {
  it("runs to its end from the dashboard", async () => {
    const context = await started({ [ONBOARDING_CORE_GATE]: true });
    for (const key of sequences["quick-tour"].steps) context.register(makeRegistration(key));

    context.startSequence("quick-tour");
    const shown: string[] = [];
    while (context.activeKey) {
      shown.push(context.activeKey);
      context.complete(context.activeKey);
    }

    expect(shown).toEqual(sequences["quick-tour"].steps);
    for (const key of shown) expect(context.getStatus(key)).toBe("completed");
  });

  it("passes over the chart while the first-reading empty state hides it", async () => {
    const context = await started({ [ONBOARDING_CORE_GATE]: true });
    context.register(makeRegistration("quick-tour.current-bg"));
    context.register(makeRegistration("quick-tour.widgets"));

    context.startSequence("quick-tour");
    const shown: string[] = [];
    while (context.activeKey) {
      shown.push(context.activeKey);
      context.complete(context.activeKey);
    }

    expect(shown).toEqual(["quick-tour.current-bg", "quick-tour.widgets"]);
    expect(context.getStatus("quick-tour.chart")).toBe("unseen");
  });

  it("raises a passed-over step once it mounts", async () => {
    const context = await started({ [ONBOARDING_CORE_GATE]: true });
    context.register(makeRegistration("quick-tour.current-bg"));
    context.startSequence("quick-tour");
    context.complete("quick-tour.current-bg");
    expect(context.activeKey).toBeNull();

    context.register(makeRegistration("quick-tour.chart"));

    expect(context.activeKey).toBe("quick-tour.chart");
  });

  it("stays shut organically for a viewer the core gate keeps out", async () => {
    const context = await started({ [ONBOARDING_CORE_GATE]: false });
    context.register(makeRegistration("quick-tour.current-bg"));
    settle();

    expect(context.activeKey).toBeNull();
  });
});

describe("stored states for retired marks", () => {
  it("are carried without disturbing the live tours", async () => {
    const context = await started({ [ONBOARDING_CORE_GATE]: true }, [
      stored("onboarding.sharing", "completed"),
      stored("setup-alerts.overview", "seen"),
      stored("dashboard-discovery.widgets", "dismissed"),
      stored("power-user.trackers", "unseen"),
    ]);
    context.register(makeRegistration(DISCOVERY_STEP));
    settle();

    expect(context.activeKey).toBe(DISCOVERY_STEP);
    expect(context.getStatus("onboarding.sharing")).toBe("completed");
  });
});

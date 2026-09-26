import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { CoachMarkContext } from "@nocturne/coach";
import type { CoachMarkAdapter, MarkRegistration, MarkState, SequenceConfig } from "@nocturne/coach";

const SETTLE = 10;

const emptyAdapter: CoachMarkAdapter = { fetchAll: async () => [], update: async () => {} };

function makeRegistration(key: string, priority = 0): MarkRegistration {
  return { key, step: 0, title: key, description: key, priority, element: {} as HTMLElement };
}

function stored(markKey: string, status: MarkState["status"]): MarkState {
  return { id: markKey, markKey, status, seenAt: null, completedAt: null };
}

async function started(sequences: SequenceConfig = {}, adapter = emptyAdapter) {
  const context = new CoachMarkContext(adapter, sequences, SETTLE);
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

describe("a mark whose element leaves the page", () => {
  it("stops being the active mark", async () => {
    const context = await started();
    const unregister = context.register(makeRegistration("page-one.tip"));
    settle();
    expect(context.activeKey).toBe("page-one.tip");

    unregister();
    await Promise.resolve();

    expect(context.activeKey).toBeNull();
  });

  it("lets the next page's mark be selected", async () => {
    const context = await started();
    const unregister = context.register(makeRegistration("page-one.tip"));
    settle();

    unregister();
    context.register(makeRegistration("page-two.tip"));
    await Promise.resolve();
    settle();

    expect(context.activeKey).toBe("page-two.tip");
  });

  it("stays active when its attachment re-runs in the same flush", async () => {
    const context = await started();
    const unregister = context.register(makeRegistration("tip"));
    settle();

    unregister();
    context.register(makeRegistration("tip"));
    await Promise.resolve();

    expect(context.activeKey).toBe("tip");
  });
});

describe("a mark completed in the background", () => {
  it("leaves the mark on screen in place", async () => {
    const context = await started();
    context.register(makeRegistration("reading", 10));
    context.register(makeRegistration("other"));
    context.register(makeRegistration("background"));
    settle();
    context.markSeen("reading");

    context.complete("background");

    expect(context.activeKey).toBe("reading");
    expect(context.getStatus("background")).toBe("completed");
  });

  it("raises nothing while quiet", async () => {
    const context = await started();
    context.register(makeRegistration("first", 10));
    context.register(makeRegistration("second"));
    context.register(makeRegistration("third"));
    settle();
    context.dismiss("first", { quiet: true });

    context.activate("second", 0);
    context.complete("second");
    settle();

    expect(context.activeKey).toBeNull();
  });
});

describe("before the stored states arrive", () => {
  function pendingAdapter() {
    let resolve: (states: MarkState[]) => void = () => {};
    const adapter: CoachMarkAdapter = {
      fetchAll: () => new Promise((r) => (resolve = r)),
      update: async () => {},
    };
    return { adapter, resolve: (states: MarkState[]) => resolve(states) };
  }

  it("selects nothing when a mark completes", () => {
    const { adapter } = pendingAdapter();
    const context = new CoachMarkContext(adapter, {}, SETTLE);
    void context.initialize();
    context.register(makeRegistration("dismissed-on-server"));
    context.register(makeRegistration("configured"));

    context.complete("configured");

    expect(context.activeKey).toBeNull();
  });

  it("reports every mark ineligible, so no dot is drawn", () => {
    const { adapter } = pendingAdapter();
    const context = new CoachMarkContext(adapter, {}, SETTLE);
    void context.initialize();

    expect(context.isMarkEligible("any")).toBe(false);
  });

  it("keeps a completion made while the fetch was in flight", async () => {
    const { adapter, resolve } = pendingAdapter();
    const context = new CoachMarkContext(adapter, {}, SETTLE);
    const initializing = context.initialize();
    context.complete("configured");

    resolve([stored("configured", "unseen"), stored("other", "dismissed")]);
    await initializing;

    expect(context.getStatus("configured")).toBe("completed");
    expect(context.getStatus("other")).toBe("dismissed");
  });

  it("stays ineligible when the fetch fails", async () => {
    const failing: CoachMarkAdapter = {
      fetchAll: () => Promise.reject(new Error("offline")),
      update: async () => {},
    };
    const error = vi.spyOn(console, "error").mockImplementation(() => {});
    const context = new CoachMarkContext(failing, {}, SETTLE);

    await context.initialize();

    expect(context.isMarkEligible("any")).toBe(false);
    error.mockRestore();
  });
});

describe("a forced sequence", () => {
  it("is not interrupted by the settle timer while it waits for a step to mount", async () => {
    const sequences: SequenceConfig = {
      tour: { priority: 100, steps: ["tour.first", "tour.second"] },
    };
    const context = await started(sequences);
    context.register(makeRegistration("standalone"));
    context.startSequence("tour");

    settle();

    expect(context.activeKey).toBeNull();
  });
});

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { CoachMarkContext } from "@nocturne/coach";
import { coachLabels } from "./copy.svelte";
import type { CoachMarkAdapter, MarkRegistration, MarkState, SequenceConfig } from "@nocturne/coach";

const SETTLE = 10;

const SEQUENCES: SequenceConfig = {
  intro: { priority: 20, steps: ["intro.one"], quietOnDismiss: true },
  tour: { priority: 10, steps: ["tour.one", "tour.two", "tour.three"] },
};

function registration(key: string): MarkRegistration {
  return { key, step: 0, title: key, description: key, priority: 0, element: {} as HTMLElement };
}

const finished = (markKey: string): MarkState => ({
  id: markKey,
  markKey,
  status: "completed",
  seenAt: null,
  completedAt: null,
});

async function started(states: MarkState[] = []) {
  const adapter: CoachMarkAdapter = { fetchAll: async () => states, update: async () => {} };
  const context = new CoachMarkContext(adapter, coachLabels(), SEQUENCES, SETTLE);
  await context.initialize();
  return context;
}

function walk(context: CoachMarkContext): string[] {
  const shown: string[] = [];
  for (let key = context.activeKey; key && shown.length < 10; key = context.activeKey) {
    shown.push(key);
    context.complete(key);
  }
  return shown;
}

beforeEach(() => {
  vi.useFakeTimers();
});

afterEach(() => {
  vi.useRealTimers();
});

describe("replaying a sequence", () => {
  it("shows every step again once the reader has finished it", async () => {
    const context = await started(SEQUENCES.tour.steps.map(finished));
    for (const key of SEQUENCES.tour.steps) context.register(registration(key));
    vi.advanceTimersByTime(SETTLE * 2);
    expect(context.activeKey).toBeNull();

    context.replaySequence("tour");

    expect(walk(context)).toEqual(SEQUENCES.tour.steps);
  });

  it("skips a step whose target is not on the page, rather than waiting for it", async () => {
    const context = await started(SEQUENCES.tour.steps.map(finished));
    context.register(registration("tour.one"));
    context.register(registration("tour.three"));

    context.replaySequence("tour");

    expect(walk(context)).toEqual(["tour.one", "tour.three"]);
  });

  it("ends on a dismiss, with nothing raised after it", async () => {
    const context = await started(SEQUENCES.tour.steps.map(finished));
    for (const key of SEQUENCES.tour.steps) context.register(registration(key));

    context.replaySequence("tour");
    context.dismiss("tour.one");
    vi.advanceTimersByTime(SETTLE * 2);

    expect(context.activeKey).toBeNull();
  });

  it("shows several sequences one after the other", async () => {
    const context = await started([...SEQUENCES.intro.steps, ...SEQUENCES.tour.steps].map(finished));
    for (const key of [...SEQUENCES.tour.steps, ...SEQUENCES.intro.steps]) context.register(registration(key));

    context.replaySequence("intro", "tour");

    expect(walk(context)).toEqual([...SEQUENCES.intro.steps, ...SEQUENCES.tour.steps]);
  });

  it("dismisses the unfinished steps of every replayed sequence on a dismiss", async () => {
    const context = await started(SEQUENCES.intro.steps.map(finished));
    for (const key of [...SEQUENCES.intro.steps, ...SEQUENCES.tour.steps]) context.register(registration(key));

    context.replaySequence("intro", "tour");
    context.dismiss("intro.one");

    for (const key of SEQUENCES.tour.steps) expect(context.getStatus(key), key).toBe("dismissed");
  });

  it("does nothing while coach marks are switched off", async () => {
    const context = await started();
    context.register(registration("tour.one"));
    context.setDisabled(true);

    context.replaySequence("tour");

    expect(context.activeKey).toBeNull();
    context.setDisabled(false);
  });
});

describe("dismissing a sequence that quiets on dismiss", () => {
  it("raises no other mark until the next navigation", async () => {
    const context = await started();
    context.register(registration("intro.one"));
    context.register(registration("tour.one"));
    vi.advanceTimersByTime(SETTLE * 2);
    expect(context.activeKey).toBe("intro.one");

    context.dismiss("intro.one");
    vi.advanceTimersByTime(SETTLE * 2);

    expect(context.activeKey).toBeNull();
    expect(context.getStatus("tour.one")).toBe("unseen");

    context.clearQuiet();
    vi.advanceTimersByTime(SETTLE * 2);

    expect(context.activeKey).toBe("tour.one");
  });

  it("leaves the next mark to come up when a sequence without it is dismissed", async () => {
    const context = await started();
    context.register(registration("tour.one"));
    context.register(registration("other"));
    vi.advanceTimersByTime(SETTLE * 2);
    expect(context.activeKey).toBe("tour.one");

    context.dismiss("tour.one");
    vi.advanceTimersByTime(SETTLE * 2);

    expect(context.activeKey).toBe("other");
  });
});

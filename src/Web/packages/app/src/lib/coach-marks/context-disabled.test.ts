import { describe, it, expect } from "vitest";
import { CoachMarkContext } from "@nocturne/coach";
import type { CoachMarkAdapter, MarkRegistration, SequenceConfig } from "@nocturne/coach";

const sequences: SequenceConfig = {
  onboarding: {
    priority: 100,
    steps: ["onboarding.a", "onboarding.b"],
  },
};

const adapter: CoachMarkAdapter = {
  fetchAll: async () => [],
  update: async () => {},
};

function makeRegistration(key: string): MarkRegistration {
  return { key, step: 0, title: key, description: key, priority: 0, element: {} as HTMLElement };
}

async function mounted(): Promise<CoachMarkContext> {
  const context = new CoachMarkContext(adapter, sequences);
  await context.initialize();
  context.register(makeRegistration("onboarding.a"));
  context.register(makeRegistration("onboarding.b"));
  context.register(makeRegistration("standalone"));
  return context;
}

describe("coach marks switched off", () => {
  it("completing a mark does not put the next one on screen", async () => {
    const context = await mounted();
    context.setDisabled(true);

    context.complete("onboarding.a");

    expect(context.activeKey).toBeNull();
    expect(context.getStatus("onboarding.a")).toBe("completed");
  });

  // The counterpart: without it the assertion above would also pass on a `complete` that never
  // advances at all, which is not the behaviour being protected.
  it("completing a mark advances to the next one when they are on", async () => {
    const context = await mounted();

    context.complete("onboarding.a");

    expect(context.activeKey).toBe("onboarding.b");
  });

  it("pressing a hotspot dot does not put its mark on screen", async () => {
    const context = await mounted();
    context.setDisabled(true);

    context.activate("standalone", 0);

    expect(context.activeKey).toBeNull();
  });
});

describe("a mark the reader has finished with", () => {
  it("cannot be put back on screen by its hotspot dot", async () => {
    const context = await mounted();
    context.dismiss("standalone", { quiet: true });

    context.activate("standalone", 0);

    expect(context.activeKey).toBeNull();
  });

  // The counterpart to both hotspot cases: without it they would also pass on an `activate` that
  // never raises anything, which is not the behaviour being protected.
  it("is the only thing that stops one — a live mark is raised", async () => {
    const context = await mounted();

    context.activate("standalone", 0);

    expect(context.activeKey).toBe("standalone");
  });
});

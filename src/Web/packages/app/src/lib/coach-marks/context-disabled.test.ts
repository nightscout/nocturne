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
});

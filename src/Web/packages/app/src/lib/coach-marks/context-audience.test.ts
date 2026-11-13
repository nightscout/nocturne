import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { CoachMarkContext, selectActiveMark } from "@nocturne/coach";
import type { CoachMarkAdapter, MarkRegistration, MarkState, SequenceConfig } from "@nocturne/coach";
import { ONBOARDING_CORE_GATE, coachAudience, sequences, type CoachAudience } from "./sequences";
import { coachLabels } from "./copy.svelte";

const SETTLE = 10;

function registration(key: string): MarkRegistration {
  return { key, step: 0, title: key, description: key, priority: 0, element: {} as HTMLElement };
}

async function started(config: SequenceConfig, audience: string | undefined, states: MarkState[] = []) {
  const adapter: CoachMarkAdapter = { fetchAll: async () => states, update: vi.fn(async () => {}) };
  const context = new CoachMarkContext(
    adapter,
    coachLabels(),
    config,
    SETTLE,
    2000,
    () => ({ [ONBOARDING_CORE_GATE]: true }),
    () => audience,
  );
  await context.initialize();
  return { context, adapter };
}

const settle = () => vi.advanceTimersByTime(SETTLE * 2);

function walk(context: CoachMarkContext): string[] {
  const shown: string[] = [];
  for (let key = context.activeKey; key && shown.length < 20; key = context.activeKey) {
    shown.push(key);
    context.complete(key);
    settle();
  }
  return shown;
}

beforeEach(() => {
  vi.useFakeTimers();
});

afterEach(() => {
  vi.useRealTimers();
});

const TWO_READERS: SequenceConfig = {
  owner: { audience: "member", priority: 20, steps: ["owner.one", "owner.two"] },
  visitor: { audience: "guest", priority: 10, steps: ["visitor.one"] },
  anyone: { priority: 5, steps: ["anyone.one"] },
};
const ALL_KEYS = Object.values(TWO_READERS).flatMap((seq) => seq.steps);

describe("a sequence written for one reader", () => {
  it("is shown to that reader, with the sequences written for every reader", async () => {
    const { context } = await started(TWO_READERS, "guest");
    for (const key of ALL_KEYS) context.register(registration(key));
    settle();

    expect(walk(context)).toEqual(["visitor.one", "anyone.one"]);
  });

  it("draws no dot for another reader, though its marks share an element with theirs", async () => {
    const { context } = await started(TWO_READERS, "guest");
    for (const key of ALL_KEYS) context.register(registration(key));

    expect(context.isMarkEligible("owner.one")).toBe(false);
    expect(context.isMarkEligible("visitor.one")).toBe(true);
    expect(context.isMarkEligible("anyone.one")).toBe(true);
  });

  it("cannot be raised from its dot by another reader", async () => {
    const { context } = await started(TWO_READERS, "guest");
    context.register(registration("owner.one"));

    context.activate("owner.one", 0);

    expect(context.activeKey).toBeNull();
  });

  it("is not started for another reader, and leaves that reader's own tour running", async () => {
    const { context } = await started(TWO_READERS, "guest");
    for (const key of ALL_KEYS) context.register(registration(key));

    context.startSequence("owner");
    settle();

    expect(context.activeKey).toBe("visitor.one");
  });

  it("is not replayed for another reader, alone or after one of their own", async () => {
    const { context } = await started(TWO_READERS, "guest");
    for (const key of ALL_KEYS) context.register(registration(key));

    context.replaySequence("owner");
    expect(context.activeKey).toBeNull();
    context.replaySequence("visitor", "owner");
    expect(context.activeKey).toBeNull();

    context.replaySequence("visitor");
    expect(context.activeKey).toBe("visitor.one");
  });

  it("is left as it was when another reader dismisses their own", async () => {
    const { context, adapter } = await started(TWO_READERS, "guest");
    for (const key of ALL_KEYS) context.register(registration(key));
    settle();

    context.dismiss("visitor.one");

    expect(context.getStatus("owner.one")).toBe("unseen");
    expect(adapter.update).not.toHaveBeenCalledWith("owner.one", expect.anything());
  });

  it("is shown to no one when the host names no reader", async () => {
    const { context } = await started(TWO_READERS, undefined);
    for (const key of ALL_KEYS) context.register(registration(key));
    settle();

    expect(walk(context)).toEqual(["anyone.one"]);
  });

  it("is skipped by selection for another reader", () => {
    const pick = (audience: string) =>
      selectActiveMark(new Map(), ALL_KEYS.map(registration), TWO_READERS, {}, audience)?.key;

    expect(pick("member")).toBe("owner.one");
    expect(pick("guest")).toBe("visitor.one");
    expect(pick("share")).toBe("anyone.one");
  });
});

describe("the reader of the app shell", () => {
  it.each<[string, Parameters<typeof coachAudience>[0], CoachAudience]>([
    ["a signed-in account", { user: { subjectId: "s" }, isGuestSession: false }, "member"],
    ["a guest link session, which carries a user", { user: { subjectId: "guest" }, isGuestSession: true }, "guest"],
    ["a public share, which has no user", { user: null, isGuestSession: false }, "share"],
  ])("is %s", (_, viewer, audience) => {
    expect(coachAudience(viewer)).toBe(audience);
  });
});

describe("the app's tours", () => {
  const namesFor = (audience: CoachAudience) =>
    Object.entries(sequences)
      .filter(([, seq]) => seq.audience === audience)
      .map(([name]) => name);
  const everyStep = Object.values(sequences).flatMap((seq) => seq.steps);

  async function organicTour(audience: CoachAudience) {
    const { context } = await started(sequences, audience);
    for (const key of everyStep) context.register(registration(key));
    settle();
    return walk(context);
  }

  it("give a guest and a share viewer one tour each", () => {
    expect(namesFor("guest")).toEqual(["guest-tour"]);
    expect(namesFor("share")).toEqual(["share-tour"]);
  });

  it("show a guest the guest tour and nothing else, with every mark in the app on the page", async () => {
    expect(await organicTour("guest")).toEqual(sequences["guest-tour"].steps);
  });

  it("show a share viewer the share tour and nothing else, with every mark in the app on the page", async () => {
    expect(await organicTour("share")).toEqual(sequences["share-tour"].steps);
  });

  it("show a member neither", async () => {
    const shown = await organicTour("member");

    expect(shown).toContain("quick-tour.current-bg");
    expect(shown.filter((key) => /^(guest|share)-tour\./.test(key))).toEqual([]);
  });

  it.each<CoachAudience>(["guest", "share"])("refuse a %s reader a member's tour named in the address", async (audience) => {
    const { context } = await started(sequences, audience);
    for (const key of everyStep) context.register(registration(key));

    context.startSequence("quick-tour");

    expect(context.activeKey).toBeNull();
  });
});

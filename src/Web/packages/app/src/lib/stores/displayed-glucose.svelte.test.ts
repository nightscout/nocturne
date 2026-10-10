import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { flushSync } from "svelte";
import { GlucoseStatus } from "$lib/api/generated/nocturne-api-client";
import {
  PAIR_HOLD_MS,
  displayedGlucose,
  refreshSummaryOnNewReading,
} from "./current-glucose-status.svelte";

const holder = vi.hoisted(() => ({
  state: undefined as
    | { current?: { current?: { mills: number; status?: unknown } } }
    | undefined,
  refresh: vi.fn(() => Promise.resolve()),
}));

vi.mock("$api/generated/summaries.generated.remote", () => ({
  getSummary: () => ({
    ready: true,
    loading: false,
    get current() {
      return holder.state?.current;
    },
    refresh: holder.refresh,
  }),
}));

const summaryState = $state<{
  current?: { current?: { mills: number; status?: GlucoseStatus } };
}>({});
holder.state = summaryState;

const source = $state({
  currentEntry: null as { mills: number } | null,
  currentBG: 0,
  bgDelta: 0,
  direction: "",
});

const roots: Array<() => void> = [];

function mount() {
  let pair!: ReturnType<typeof displayedGlucose>;
  roots.push(
    $effect.root(() => {
      refreshSummaryOnNewReading(
        () => source.currentEntry?.mills,
        () => true
      );
      pair = displayedGlucose(source as never);
    })
  );
  flushSync();
  return pair;
}

function reading(mills: number, bg: number) {
  source.currentEntry = { mills };
  source.currentBG = bg;
  source.bgDelta = 2;
  source.direction = "Flat";
  flushSync();
}

function summaryLands(mills: number, status: GlucoseStatus) {
  summaryState.current = { current: { mills, status } };
  flushSync();
}

beforeEach(() => {
  vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "Date"] });
  summaryState.current = undefined;
  source.currentEntry = null;
  source.currentBG = 0;
});

afterEach(() => {
  while (roots.length) roots.pop()!();
  holder.refresh.mockReset();
  holder.refresh.mockImplementation(() => Promise.resolve());
  vi.useRealTimers();
});

describe("displayedGlucose", () => {
  it("shows the first reading with no status until its summary lands", () => {
    const pair = mount();
    reading(1_000_000, 100);

    expect(pair.currentBG).toBe(100);
    expect(pair.status).toBeUndefined();

    summaryLands(1_000_000, GlucoseStatus.InRange);
    expect(pair.status).toBe(GlucoseStatus.InRange);
  });

  it("holds the previous value and status until the summary matches the new reading", () => {
    const pair = mount();
    reading(1_000_000, 100);
    summaryLands(1_000_000, GlucoseStatus.InRange);

    reading(1_300_000, 250);
    expect(pair.currentBG).toBe(100);
    expect(pair.status).toBe(GlucoseStatus.InRange);
    expect(pair.mills).toBe(1_000_000);

    summaryLands(1_300_000, GlucoseStatus.High);
    expect(pair.currentBG).toBe(250);
    expect(pair.status).toBe(GlucoseStatus.High);
  });

  it("falls back to the new value with no status once the hold expires", () => {
    const pair = mount();
    reading(1_000_000, 100);
    summaryLands(1_000_000, GlucoseStatus.InRange);
    reading(1_300_000, 250);

    vi.advanceTimersByTime(PAIR_HOLD_MS - 1);
    flushSync();
    expect(pair.currentBG).toBe(100);

    vi.advanceTimersByTime(1);
    flushSync();
    expect(pair.currentBG).toBe(250);
    expect(pair.status).toBeUndefined();
  });

  it("falls back at once when the refresh rejects", async () => {
    const pair = mount();
    reading(1_000_000, 100);
    summaryLands(1_000_000, GlucoseStatus.InRange);
    holder.refresh.mockImplementation(() => Promise.reject(new Error("down")));

    reading(1_300_000, 250);
    await vi.advanceTimersByTimeAsync(0);
    flushSync();

    expect(pair.currentBG).toBe(250);
    expect(pair.status).toBeUndefined();
  });

  it("does not hold a previous reading that was already stale", () => {
    const pair = mount();
    reading(1_000_000, 100);
    summaryLands(1_000_000, GlucoseStatus.InRange);

    reading(1_000_000 + 11 * 60 * 1000, 250);

    expect(pair.currentBG).toBe(250);
    expect(pair.status).toBeUndefined();
  });

  it("does not bring an older reading back after a hold expired (refresh hangs)", () => {
    holder.refresh.mockImplementation(() => new Promise(() => {}));
    const pair = mount();
    reading(1_000_000, 100);
    summaryLands(1_000_000, GlucoseStatus.InRange);

    reading(1_060_000, 60);
    expect(pair.currentBG).toBe(100);
    vi.advanceTimersByTime(PAIR_HOLD_MS);
    flushSync();
    expect(pair.currentBG).toBe(60);
    expect(pair.status).toBeUndefined();

    reading(1_120_000, 50);
    expect(pair.currentBG).toBe(50);
    expect(pair.mills).toBe(1_120_000);
    expect(pair.status).toBeUndefined();
  });

  it("does not bring an older reading back after a failed refresh", async () => {
    const pair = mount();
    reading(1_000_000, 100);
    summaryLands(1_000_000, GlucoseStatus.InRange);

    holder.refresh.mockImplementation(() => Promise.reject(new Error("down")));
    reading(1_300_000, 60);
    await vi.advanceTimersByTimeAsync(0);
    flushSync();
    expect(pair.currentBG).toBe(60);

    holder.refresh.mockImplementation(() => new Promise(() => {}));
    reading(1_000_000 + 590_000, 55);
    expect(pair.currentBG).toBe(55);
    expect(pair.status).toBeUndefined();
  });

  function observe(pair: ReturnType<typeof displayedGlucose>) {
    const seen: Array<[number, GlucoseStatus | undefined]> = [];
    let value!: () => number;
    let status!: () => GlucoseStatus | undefined;
    roots.push(
      $effect.root(() => {
        const v = $derived(pair.currentBG);
        const s = $derived(pair.status);
        value = () => v;
        status = () => s;
        $effect(() => {
          seen.push([v, s]);
        });
      })
    );
    flushSync();
    return { seen, value, status };
  }

  it("flips value and status together through a derived when the timer fires", () => {
    const pair = mount();
    reading(1_000_000, 100);
    summaryLands(1_000_000, GlucoseStatus.InRange);
    reading(1_400_000, 250);
    const view = observe(pair);
    expect(view.seen.at(-1)).toEqual([100, GlucoseStatus.InRange]);

    vi.advanceTimersByTime(PAIR_HOLD_MS);
    flushSync();

    expect(view.seen.at(-1)).toEqual([250, undefined]);
    expect(view.seen).not.toContainEqual([250, GlucoseStatus.InRange]);
    expect(view.seen).not.toContainEqual([100, undefined]);
  });

  it("does not expire on the clock alone: a stalled timer leaves the derived pair intact", () => {
    const pair = mount();
    reading(1_000_000, 100);
    summaryLands(1_000_000, GlucoseStatus.InRange);
    reading(1_400_000, 250);
    const view = observe(pair);

    vi.setSystemTime(Date.now() + PAIR_HOLD_MS + 1);
    flushSync();

    expect([view.value(), view.status()]).toEqual([100, GlucoseStatus.InRange]);
  });

  it("expires at once on visibilitychange when the timer stalled, value and status together", () => {
    const pair = mount();
    reading(1_000_000, 100);
    summaryLands(1_000_000, GlucoseStatus.InRange);
    reading(1_400_000, 250);
    const view = observe(pair);

    vi.setSystemTime(Date.now() + PAIR_HOLD_MS + 1);
    document.dispatchEvent(new Event("visibilitychange"));
    flushSync();

    expect(view.seen.at(-1)).toEqual([250, undefined]);
    expect(view.seen).not.toContainEqual([250, GlucoseStatus.InRange]);
  });

  it("holds a fresh reading again after a failed refresh's summary lands late", async () => {
    const pair = mount();
    reading(1_000_000, 100);
    summaryLands(1_000_000, GlucoseStatus.InRange);
    holder.refresh.mockImplementation(() => Promise.reject(new Error("down")));
    reading(1_060_000, 90);
    await vi.advanceTimersByTimeAsync(0);
    flushSync();
    summaryLands(1_060_000, GlucoseStatus.InRange);

    holder.refresh.mockImplementation(() => new Promise(() => {}));
    reading(1_120_000, 80);
    expect(pair.currentBG).toBe(90);
    expect(pair.status).toBe(GlucoseStatus.InRange);
  });
});

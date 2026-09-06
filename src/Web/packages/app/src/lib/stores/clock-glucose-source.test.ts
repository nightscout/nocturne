import { describe, it, expect } from "vitest";
import type { Entry } from "$lib/websocket/types";
import { clockGlucoseSourceOf } from "./realtime-store.svelte";

const MILLS = Date.UTC(2026, 11, 31, 14, 5);

type ClockSourceStore = Parameters<typeof clockGlucoseSourceOf>[0];

/**
 * The store's own `currentBG`, `lastUpdated` and `bgDelta` carry 0 and the
 * current time for the dashboard, so a source built over those could not tell
 * an absent reading from a real one. This stands in the entries they are
 * derived from instead.
 */
function storeWith(entries: Entry[]): ClockSourceStore {
  const sorted = [...entries].sort((a, b) => (b.mills || 0) - (a.mills || 0));
  return {
    currentEntry: sorted[0] ?? null,
    previousEntry: sorted[1] ?? null,
    direction: sorted[0]?.direction ?? "",
    demoMode: false,
  };
}

describe("clockGlucoseSourceOf", () => {
  it("has no reading, age or delta when the store holds no entries", () => {
    const source = clockGlucoseSourceOf(storeWith([]));

    expect(source.currentBG).toBeNull();
    expect(source.lastUpdated).toBeNull();
    expect(source.bgDelta).toBeNull();
  });

  it("reads the newest entry's value and time", () => {
    const source = clockGlucoseSourceOf(
      storeWith([
        { sgv: 120, mills: MILLS, direction: "Flat" },
        { sgv: 200, mills: MILLS - 60 * 60_000 },
      ])
    );

    expect(source.currentBG).toBe(120);
    expect(source.lastUpdated).toBe(MILLS);
    expect(source.direction).toBe("Flat");
  });

  it("falls back to mgdl on an entry carrying no sgv", () => {
    const source = clockGlucoseSourceOf(
      storeWith([{ mgdl: 88, mills: MILLS }])
    );

    expect(source.currentBG).toBe(88);
  });

  it("claims no change from a lone reading", () => {
    const source = clockGlucoseSourceOf(
      storeWith([{ sgv: 120, mills: MILLS }])
    );

    expect(source.bgDelta).toBeNull();
  });

  it("measures the change against the previous reading", () => {
    const source = clockGlucoseSourceOf(
      storeWith([
        { sgv: 120, mills: MILLS },
        { sgv: 113, mills: MILLS - 5 * 60_000 },
      ])
    );

    expect(source.bgDelta).toBe(7);
  });

  it("prefers the delta the reading carried", () => {
    const source = clockGlucoseSourceOf(
      storeWith([
        { sgv: 120, mills: MILLS, delta: 4 },
        { sgv: 113, mills: MILLS - 5 * 60_000 },
      ])
    );

    expect(source.bgDelta).toBe(4);
  });

  it("follows the store rather than sampling it once", () => {
    const store = storeWith([]);
    const source = clockGlucoseSourceOf(store);

    expect(source.currentBG).toBeNull();

    Object.assign(store, {
      currentEntry: { sgv: 99, mills: MILLS },
    });

    expect(source.currentBG).toBe(99);
  });
});

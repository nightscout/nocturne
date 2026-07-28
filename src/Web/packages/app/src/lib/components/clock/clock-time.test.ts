/**
 * The clock surfaces used to each resolve 12h/24h their own way — one hardcoded
 * 12-hour, one reading only the element's format. These cover the shared rule.
 */
import { describe, it, expect, vi } from "vitest";
import type { TimeFormat } from "$lib/stores/appearance-store.svelte";

// formatting.ts pulls in appearance-store, which pulls in mode-watcher (no node
// export). Stub the chain, same as formatting.test.ts.
vi.mock("$app/environment", () => ({ browser: false }));
vi.mock("mode-watcher", () => ({}));
vi.mock("runed", () => ({
  PersistedState: class {
    current: unknown;
    constructor(v: unknown) {
      this.current = v;
    }
  },
}));
vi.mock("$lib/stores/appearance-store.svelte", () => ({
  glucoseUnits: { current: "mg/dl" },
  timeFormat: { current: "12" },
  regionFormat: { current: "" },
  preferredLanguage: { current: "en" },
}));

const { formatClockTime, DEFAULT_CLOCK_TIME_FORMAT } = await import("./clock-time");
const store = await import("$lib/stores/appearance-store.svelte");

describe("formatClockTime", () => {
  const afternoon = new Date(2026, 11, 31, 14, 5);

  function withTimeFormat(value: TimeFormat, run: () => void) {
    const previous = store.timeFormat.current;
    store.timeFormat.current = value;
    try {
      run();
    } finally {
      store.timeFormat.current = previous;
    }
  }

  it("defaults new elements to following the preference", () => {
    expect(DEFAULT_CLOCK_TIME_FORMAT).toBe("auto");
  });

  it("follows the time-format preference when set to auto", () => {
    withTimeFormat("24", () =>
      expect(formatClockTime(afternoon, "auto")).toBe("14:05"),
    );
    withTimeFormat("12", () =>
      expect(formatClockTime(afternoon, "auto")).toMatch(/^02:05\s?[Pp]/),
    );
  });

  it("follows the preference for a face saved before auto existed", () => {
    withTimeFormat("24", () =>
      expect(formatClockTime(afternoon, undefined)).toBe("14:05"),
    );
  });

  it("honours an explicit pin regardless of the preference", () => {
    withTimeFormat("24", () =>
      expect(formatClockTime(afternoon, "12h")).toMatch(/^02:05\s?[Pp]/),
    );
    withTimeFormat("12", () =>
      expect(formatClockTime(afternoon, "24h")).toBe("14:05"),
    );
  });
});

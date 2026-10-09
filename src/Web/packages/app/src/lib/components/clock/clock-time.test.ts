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

const { formatClockTime } = await import("./clock-time");
const store = await import("$lib/stores/appearance-store.svelte");

describe("formatClockTime", () => {
  const afternoon = new Date(2026, 11, 31, 14, 5);

  function withViewerTimeFormat(value: TimeFormat, run: () => void) {
    const previous = store.timeFormat.current;
    store.timeFormat.current = value;
    try {
      run();
    } finally {
      store.timeFormat.current = previous;
    }
  }

  it("renders in the face's time format whatever the viewer prefers", () => {
    withViewerTimeFormat("12", () =>
      expect(formatClockTime(afternoon, "24")).toBe("14:05"),
    );
    withViewerTimeFormat("24", () =>
      expect(formatClockTime(afternoon, "12")).toMatch(/^02:05\s?[Pp]/),
    );
  });

  it("reads a face saved before it carried a time format as 12 hour", () => {
    withViewerTimeFormat("24", () =>
      expect(formatClockTime(afternoon, undefined)).toMatch(/^02:05\s?[Pp]/),
    );
  });
});

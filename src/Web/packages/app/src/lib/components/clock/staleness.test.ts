import { describe, it, expect, vi } from "vitest";
import type { RegionFormat } from "#lib/stores/appearance-store.svelte.js";

vi.mock("$app/env", () => ({ browser: false }));
vi.mock("mode-watcher", () => ({}));
vi.mock("runed", () => ({
  PersistedState: class {
    current: unknown;
    constructor(v: unknown) {
      this.current = v;
    }
  },
}));
vi.mock("#lib/stores/appearance-store.svelte.js", () => ({
  glucoseUnits: { current: "mg/dl" },
  timeFormat: { current: "12" },
  regionFormat: { current: "" },
  preferredLanguage: { current: "en" },
}));

const {
  isClockReadingStale,
  readingAgeLabel,
  readingAgeMinutes,
  readingAgePhrase,
} = await import("./staleness");
const store = await import("#lib/stores/appearance-store.svelte.js");

const narrow = (locale: string) =>
  new Intl.RelativeTimeFormat(locale, { style: "narrow", numeric: "auto" });

const MIN = 60_000;
const lastUpdated = 1_700_000_000_000;

describe("readingAgeMinutes", () => {
  it("floors to whole minutes", () => {
    expect(readingAgeMinutes(lastUpdated, lastUpdated + 59_999)).toBe(0);
    expect(readingAgeMinutes(lastUpdated, lastUpdated + MIN)).toBe(1);
    expect(readingAgeMinutes(lastUpdated, lastUpdated + 7.9 * MIN)).toBe(7);
  });

  it("clamps a reading timestamped in the future to 0", () => {
    expect(readingAgeMinutes(lastUpdated, lastUpdated - 5 * MIN)).toBe(0);
  });
});

describe("isClockReadingStale", () => {
  it("is disabled when staleMinutes is unset or 0", () => {
    const hoursLater = lastUpdated + 300 * MIN;
    expect(isClockReadingStale(undefined, lastUpdated, hoursLater)).toBe(false);
    expect(isClockReadingStale(0, lastUpdated, hoursLater)).toBe(false);
  });

  it("turns true once the age reaches the configured window", () => {
    expect(isClockReadingStale(15, lastUpdated, lastUpdated + 14 * MIN)).toBe(
      false
    );
    expect(isClockReadingStale(15, lastUpdated, lastUpdated + 15 * MIN)).toBe(
      true
    );
  });

  it("keeps advancing while lastUpdated stays put", () => {
    // A stalled CGM re-serves the same reading, so only `now` moves. This is the
    // case a Date.now() read inside a derivation never recomputed for.
    expect(isClockReadingStale(15, lastUpdated, lastUpdated)).toBe(false);
    expect(isClockReadingStale(15, lastUpdated, lastUpdated + 60 * MIN)).toBe(
      true
    );
  });

  it("is never stale without a reading to have aged", () => {
    expect(isClockReadingStale(15, null, lastUpdated + 60 * MIN)).toBe(false);
  });
});

describe("readingAgeLabel", () => {
  it("reads 'now' under a minute", () => {
    expect(readingAgeLabel(lastUpdated, lastUpdated + 30_000)).toBe("now");
  });

  it("reads whole minutes above a minute", () => {
    expect(readingAgeLabel(lastUpdated, lastUpdated + MIN)).toBe("1m");
    expect(readingAgeLabel(lastUpdated, lastUpdated + 125 * MIN)).toBe("125m");
  });
});

describe("readingAgePhrase", () => {
  it("reads under a minute as now", () => {
    expect(readingAgePhrase(lastUpdated, lastUpdated + 30_000)).toBe(
      narrow("en").format(0, "second")
    );
  });

  it("names the largest whole unit, not a count of minutes", () => {
    expect(readingAgePhrase(lastUpdated, lastUpdated + 7 * MIN)).toBe(
      narrow("en").format(-7, "minute")
    );
    expect(readingAgePhrase(lastUpdated, lastUpdated + 125 * MIN)).toBe(
      narrow("en").format(-2, "hour")
    );
  });

  it("writes the words in the regional format's language", () => {
    const previous = store.regionFormat.current;
    store.regionFormat.current = "de-DE" as RegionFormat;
    try {
      expect(readingAgePhrase(lastUpdated, lastUpdated + 7 * MIN)).toBe(
        narrow("de").format(-7, "minute")
      );
    } finally {
      store.regionFormat.current = previous;
    }
  });
});

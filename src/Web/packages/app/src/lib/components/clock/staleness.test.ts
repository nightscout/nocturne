import { describe, it, expect } from "vitest";
import {
  isClockReadingStale,
  readingAgeLabel,
  readingAgeMinutes,
} from "./staleness";

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

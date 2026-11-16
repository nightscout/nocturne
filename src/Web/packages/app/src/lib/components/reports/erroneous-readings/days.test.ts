import { describe, expect, it } from "vitest";
import { ErroneousEpisodeKind, type ErroneousReadingEpisode } from "#lib/api/index.js";
import { daySummary } from "./days";

const readings = (invalid: number): ErroneousReadingEpisode => ({
  kind: ErroneousEpisodeKind.Readings,
  invalidCount: invalid,
});

describe("daySummary", () => {
  it("names the episodes, the invalid readings and the lone ones", () => {
    const summary = daySummary({
      episodes: [readings(20), readings(8)],
      isolatedReadingMills: [1],
      invalidReadings: 29,
    });

    expect(summary).toBe("2 episodes · 29 invalid readings · 1 on its own");
  });

  it("names only the lone readings on a day with no episode", () => {
    expect(daySummary({ episodes: [], isolatedReadingMills: [1, 2, 3], invalidReadings: 3 })).toBe(
      "3 invalid readings · 3 on their own"
    );
  });

  it("leaves out readings for a day with only a span, which has none", () => {
    const summary = daySummary({
      episodes: [{ kind: ErroneousEpisodeKind.Span, invalidCount: 0 }],
      isolatedReadingMills: [],
      invalidReadings: 0,
    });

    expect(summary).toBe("1 episode");
  });
});

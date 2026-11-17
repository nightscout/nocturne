import { describe, expect, it } from "vitest";
import { DayOfWeek, LoopAnalysisComparison } from "#lib/api/index.js";
import {
  customComparisonProblem,
  dailyProfileQuery,
  readLoopAnalysisFilter,
  withoutUnavailable,
  writeLoopAnalysisFilter,
  type LoopAnalysisFilterState,
} from "./loop-analysis-filter";

const bare = () => readLoopAnalysisFilter(new URLSearchParams());
const range = { startDate: "2026-10-09", endDate: "2026-10-22" };

function roundTrip(filter: LoopAnalysisFilterState, base = "") {
  return readLoopAnalysisFilter(writeLoopAnalysisFilter(filter, new URLSearchParams(base)));
}

describe("loop analysis filter URL", () => {
  it("reads a bare URL as no comparison, every day and no masks set", () => {
    expect(bare()).toEqual({
      compare: LoopAnalysisComparison.None,
      compareFrom: null,
      compareTo: null,
      weekdays: [],
      profileName: null,
      masks: {},
    });
  });

  it("writes nothing for a bare filter", () => {
    expect(writeLoopAnalysisFilter(bare(), new URLSearchParams()).toString()).toBe("");
  });

  it("round-trips every filter, a mask set false included", () => {
    const filter: LoopAnalysisFilterState = {
      compare: LoopAnalysisComparison.Custom,
      compareFrom: "2026-06-01",
      compareTo: "2026-07-15",
      weekdays: [DayOfWeek.Sunday, DayOfWeek.Saturday],
      profileName: "Weekend & travel",
      masks: { excludeOverrides: true, excludeIllness: false, excludeOpenLoop: false },
    };
    expect(roundTrip(filter)).toEqual(filter);
  });

  it("round-trips the previous-period comparison without custom dates", () => {
    const filter = { ...bare(), compare: LoopAnalysisComparison.Previous, compareFrom: "2026-01-01" };
    const search = writeLoopAnalysisFilter(filter, new URLSearchParams());
    expect(search.get("compare")).toBe("previous");
    expect(search.has("compareFrom")).toBe(false);
    expect(readLoopAnalysisFilter(search)).toEqual({ ...filter, compareFrom: null });
  });

  it("keeps the shared report range and other parameters it does not own", () => {
    const search = writeLoopAnalysisFilter(
      { ...bare(), weekdays: [DayOfWeek.Monday] },
      new URLSearchParams("days=30&from=2026-09-23&to=2026-10-22&isDefault=false&weekdays=5")
    );
    expect(search.get("days")).toBe("30");
    expect(search.get("from")).toBe("2026-09-23");
    expect(search.get("isDefault")).toBe("false");
    expect(search.getAll("weekdays")).toEqual(["1"]);
  });

  it("drops malformed values rather than passing them to the API", () => {
    const filter = readLoopAnalysisFilter(
      new URLSearchParams("compare=sideways&compareFrom=yesterday&weekdays=1,9,x,1&excludeIllness=maybe")
    );
    expect(filter.compare).toBe(LoopAnalysisComparison.None);
    expect(filter.compareFrom).toBeNull();
    expect(filter.weekdays).toEqual([DayOfWeek.Monday]);
    expect(filter.masks).toEqual({});
  });
});

describe("daily profile query", () => {
  it("sends the local dates and nothing the URL does not set", () => {
    expect(dailyProfileQuery(bare(), range)).toEqual(range);
  });

  it("sends a mask the URL sets, false included, so the API's default can be overridden", () => {
    const query = dailyProfileQuery({ ...bare(), masks: { excludeIllness: false } }, range);
    expect(query.excludeIllness).toBe(false);
    expect(query.excludeOpenLoop).toBeUndefined();
  });

  it("leaves the previous period to the API", () => {
    const query = dailyProfileQuery({ ...bare(), compare: LoopAnalysisComparison.Previous }, range);
    expect(query.compare).toBe(LoopAnalysisComparison.Previous);
    expect(query.compareStartDate).toBeUndefined();
  });

  it("sends a custom comparison only once it is complete and in order", () => {
    const custom = { ...bare(), compare: LoopAnalysisComparison.Custom };
    const incomplete = { ...custom, compareFrom: "2026-06-01" };
    const reversed = { ...custom, compareFrom: "2026-07-15", compareTo: "2026-06-01" };
    const ready = { ...custom, compareFrom: "2026-06-01", compareTo: "2026-07-15" };

    expect(customComparisonProblem(incomplete)).toBe("incomplete");
    expect(dailyProfileQuery(incomplete, range).compare).toBeUndefined();
    expect(customComparisonProblem(reversed)).toBe("reversed");
    expect(dailyProfileQuery(reversed, range).compareStartDate).toBeUndefined();
    expect(customComparisonProblem(ready)).toBeNull();
    expect(dailyProfileQuery(ready, range)).toMatchObject({
      compare: LoopAnalysisComparison.Custom,
      compareStartDate: "2026-06-01",
      compareEndDate: "2026-07-15",
    });
  });

  it("passes weekdays and profile through", () => {
    const query = dailyProfileQuery(
      { ...bare(), weekdays: [DayOfWeek.Saturday], profileName: "Default" },
      range
    );
    expect(query.days).toEqual([DayOfWeek.Saturday]);
    expect(query.profileName).toBe("Default");
  });
});

describe("withoutUnavailable", () => {
  it("drops the filters the caller cannot use and keeps the rest", () => {
    const filter = { ...bare(), profileName: "Default", masks: { excludeOpenLoop: true, excludeIllness: false } };
    expect(withoutUnavailable(filter, ["profileName", "excludeOpenLoop", "excludeOverrides"])).toEqual({
      ...filter,
      profileName: null,
      masks: { excludeIllness: false },
    });
  });

  it("leaves a filter that sets none of them alone", () => {
    expect(withoutUnavailable({ ...bare(), masks: { excludeIllness: true } }, ["excludeOpenLoop"])).toBeNull();
  });
});

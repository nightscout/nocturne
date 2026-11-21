import { describe, expect, it } from "vitest";
import { TrackerMode } from "@nocturne/api-client";
import { joinHours, splitHours, thresholdIssue } from "./threshold-time";

describe("threshold time", () => {
  it.each([
    [-24, { amount: 1, unit: "days", side: "before" }],
    [240, { amount: 10, unit: "days", side: "after" }],
    [251, { amount: 251, unit: "hours", side: "after" }],
    [-2, { amount: 2, unit: "hours", side: "before" }],
    [0, { amount: 0, unit: "hours", side: "after" }],
  ])("reads %i hours as a person enters them, and back", (hours, time) => {
    expect(splitHours(hours, TrackerMode.Duration)).toEqual(time);
    expect(joinHours(splitHours(hours, TrackerMode.Duration))).toBe(hours);
  });

  it("starts a threshold with no time after the start, or before an event's date", () => {
    expect(splitHours(undefined, TrackerMode.Duration).side).toBe("after");
    expect(splitHours(undefined, TrackerMode.Event).side).toBe("before");
  });

  it("stores whole hours, and nothing for an amount that is not one", () => {
    expect(joinHours({ amount: 1.5, unit: "days", side: "before" })).toBe(-36);
    expect(joinHours({ amount: 1.4, unit: "hours", side: "after" })).toBe(1);
    expect(joinHours({ amount: undefined, unit: "days", side: "after" })).toBeUndefined();
    expect(joinHours({ amount: Number.NaN, unit: "days", side: "after" })).toBeUndefined();
    expect(Object.is(joinHours({ amount: 0, unit: "days", side: "before" }), 0)).toBe(true);
  });

  it("names what the server refuses", () => {
    expect(thresholdIssue(-24, TrackerMode.Duration, undefined)).toBe("no-lifespan");
    expect(thresholdIssue(-240, TrackerMode.Duration, 240)).toBe("before-start");
    expect(thresholdIssue(-24, TrackerMode.Duration, 240)).toBeNull();
    expect(thresholdIssue(-500, TrackerMode.Event, undefined)).toBeNull();
    expect(thresholdIssue(undefined, TrackerMode.Duration, undefined)).toBeNull();
  });
});

import { describe, it, expect } from "vitest";

import { inspectionSearchWindow } from "./inspection-window";

const FIVE_MINUTES = 5 * 60 * 1000;

describe("inspectionSearchWindow", () => {
  it("spans five minutes either side of the inspected instant", () => {
    const timestamp = new Date("2026-09-02T12:00:00.000Z");

    const { from, to } = inspectionSearchWindow(timestamp);

    expect(from.toISOString()).toBe("2026-09-02T11:55:00.000Z");
    expect(to.toISOString()).toBe("2026-09-02T12:05:00.000Z");
  });

  it("stays symmetrical across a DST boundary", () => {
    // 2026-10-25T00:58Z sits either side of the UK autumn change, so a window built by
    // calendar arithmetic rather than epoch milliseconds would come out lopsided.
    const timestamp = new Date("2026-10-25T00:58:00.000Z");

    const { from, to } = inspectionSearchWindow(timestamp);

    expect(timestamp.getTime() - from.getTime()).toBe(FIVE_MINUTES);
    expect(to.getTime() - timestamp.getTime()).toBe(FIVE_MINUTES);
  });

  it("leaves the inspected instant untouched", () => {
    const timestamp = new Date("2026-09-02T12:00:00.000Z");

    inspectionSearchWindow(timestamp);

    expect(timestamp.toISOString()).toBe("2026-09-02T12:00:00.000Z");
  });
});

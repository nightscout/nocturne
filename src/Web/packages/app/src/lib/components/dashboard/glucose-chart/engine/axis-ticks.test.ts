import { describe, it, expect } from "vitest";
import { scaleTime } from "d3-scale";
import { hourTicks } from "./axis-ticks";
import { hourLabel } from "$lib/utils/formatting";

// The dashboard's blocking window; see the page loader's INITIAL_HOURS.
function window(hours: number, width = 960) {
  const end = new Date(2026, 7, 29, 21, 0);
  const start = new Date(end.getTime() - hours * 60 * 60 * 1000);
  return scaleTime().domain([start, end]).range([0, width]);
}

describe("hourTicks", () => {
  it("keeps only ticks that land on an hour", () => {
    for (const hours of [1, 3, 6, 24, 48]) {
      const ticks = hourTicks(window(hours));
      expect(ticks.length, `${hours}h`).toBeGreaterThan(0);
      for (const t of ticks) {
        expect([t.getMinutes(), t.getSeconds(), t.getMilliseconds()]).toEqual([
          0, 0, 0,
        ]);
      }
    }
  });

  // A custom `format` function makes layerchart skip filterTicksByFormat, so the
  // scale's raw sub-hour ticks would each render the same hour label twice in a
  // row. A window longer than a day repeats an hour on the next day, which is
  // why this checks neighbours rather than uniqueness.
  it("never labels two neighbouring ticks the same", () => {
    for (const hours of [1, 3, 6, 24, 48]) {
      const labels = hourTicks(window(hours)).map(hourLabel);
      const repeated = labels.filter((l, i) => i > 0 && l === labels[i - 1]);
      expect(repeated, `${hours}h`).toEqual([]);
    }
  });

  it("scales the tick count with the axis width", () => {
    const narrow = hourTicks(window(24, 320));
    const wide = hourTicks(window(24, 1600));
    expect(wide.length).toBeGreaterThan(narrow.length);
  });

  it("still yields ticks on a very narrow axis", () => {
    expect(hourTicks(window(6, 40)).length).toBeGreaterThan(0);
  });
});

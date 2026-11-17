import { describe, expect, it } from "vitest";
import { blockAt, blockSpan, episodeSearch, matchingBlock, valueDomain } from "./settings-evidence-view";

describe("settings evidence view", () => {
  it("places a block on the slot axis by the hours the API gave it", () => {
    expect(blockSpan({ startHour: 6, endHour: 11 })).toEqual({ start: 72, end: 132 });
    const blocks = [
      { startHour: 0, endHour: 6 },
      { startHour: 6, endHour: 11 },
    ];
    expect(blockAt(blocks, 71)).toBe(blocks[0]);
    expect(blockAt(blocks, 72)).toBe(blocks[1]);
    expect(blockAt(blocks, 132)).toBeUndefined();
  });

  it("finds the compared range's block by its hours", () => {
    const compared = [{ startHour: 2, endHour: 4, observed: 1 }];
    expect(matchingBlock(compared, { startHour: 2, endHour: 4 })).toBe(compared[0]);
    expect(matchingBlock(compared, { startHour: 4, endHour: 6 })).toBeUndefined();
    expect(matchingBlock(null, { startHour: 2, endHour: 4 })).toBeUndefined();
  });

  it("links an episode to Day in Review at its local day and start", () => {
    expect(episodeSearch({ date: "2026-06-12", time: "14:00" })).toBe("date=2026-06-12&time=14:00");
  });

  it("fits every value and spread, in the axis's units, from zero up", () => {
    expect(valueDomain([{ profile: 50, observed: 41, spread: { p25: 37, p75: 60 } }], 40)).toEqual([0, 66]);
    expect(valueDomain([], 10)).toEqual([0, 11]);
    const [, high] = valueDomain([{ observed: 54 }], 40, (v) => v / 18);
    expect(high).toBeCloseTo(3.3);
  });
});

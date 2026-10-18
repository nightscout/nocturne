import { describe, expect, it } from "vitest";
import { startOfLocalDay } from "./now";

describe("startOfLocalDay", () => {
  it("returns local midnight of the given instant's day", () => {
    const afternoon = new Date(2026, 9, 5, 13, 45, 12).getTime();

    expect(startOfLocalDay(afternoon)).toBe(new Date(2026, 9, 5).getTime());
  });

  it("leaves local midnight itself unchanged", () => {
    const midnight = new Date(2026, 9, 5).getTime();

    expect(startOfLocalDay(midnight)).toBe(midnight);
  });
});

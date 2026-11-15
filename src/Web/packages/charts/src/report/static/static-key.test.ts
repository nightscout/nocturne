import { describe, expect, it } from "vitest";
import { placeStaticKey, staticKeyRows, staticPadding, STATIC_KEY, STATIC_KEY_ROW } from "./StaticFrame.svelte";

const item = (label: string) => ({ label, color: "#000", shape: "line" as const });

describe("a static chart's key", () => {
  it("wraps entries that would run past the image's edge onto further rows", () => {
    const key = [item("a".repeat(40)), item("b".repeat(40)), item("c".repeat(40))];

    expect(placeStaticKey(key, 700).map((p) => p.row)).toEqual([0, 0, 1]);
    expect(staticKeyRows(key, 700)).toBe(2);
    expect(staticKeyRows([], 700)).toBe(0);
  });

  it("makes room under the plot for every row", () => {
    expect(staticPadding({ key: 3 }).bottom - staticPadding({ key: false }).bottom).toBe(STATIC_KEY + 2 * STATIC_KEY_ROW);
    expect(staticPadding({ key: true }).bottom).toBe(staticPadding({ key: 1 }).bottom);
  });
});

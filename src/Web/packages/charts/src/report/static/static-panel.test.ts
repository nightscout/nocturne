import { describe, expect, it } from "vitest";
import type { ReportLayer } from "../types.js";
import { mergedBars } from "./static-panel.js";

const weekday = { name: "weekday", label: "Weekday", kind: "category" as const };

function bars(id: string, field: string, rows: { weekday: string; value: number }[]): ReportLayer {
  return {
    id,
    title: id,
    mark: "bars",
    bindings: { x: "weekday", y: [field] },
    xKind: "category",
    unit: "U",
    side: "left",
    status: "ok",
    window: { from: 0, to: 1 },
    fields: [weekday, { name: field, label: field, kind: "number", unit: "U", color: "insulin-bolus" }],
    rows: rows.map((r) => ({ weekday: r.weekday, [field]: r.value })),
  };
}

describe("a category chart's image", () => {
  it("draws every bars layer, each layer's fields side by side in each category", () => {
    const merged = mergedBars([
      bars("Bolus", "bolus", [{ weekday: "Mon", value: 10 }, { weekday: "Tue", value: 12 }]),
      bars("Basal", "basal", [{ weekday: "Tue", value: 20 }, { weekday: "Wed", value: 21 }]),
    ]);

    expect(merged?.bindings.y).toEqual(["0.bolus", "1.basal"]);
    expect(merged?.fields.slice(1).map((f) => f.label)).toEqual(["Bolus · bolus", "Basal · basal"]);
    expect(merged?.rows).toEqual([
      { category: "Mon", "0.bolus": 10 },
      { category: "Tue", "0.bolus": 12, "1.basal": 20 },
      { category: "Wed", "1.basal": 21 },
    ]);
  });

  it("draws one bars layer as it is, and none when there is none", () => {
    const one = bars("Bolus", "bolus", [{ weekday: "Mon", value: 10 }]);

    expect(mergedBars([one])).toBe(one);
    expect(mergedBars([])).toBeNull();
  });
});

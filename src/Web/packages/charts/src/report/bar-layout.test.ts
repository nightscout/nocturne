import { describe, expect, it } from "vitest";
import { barLayout, NEUTRAL_BAR } from "./bar-layout.js";
import type { ReportField } from "./types.js";

const field = (name: string, extra: Partial<ReportField> = {}): ReportField => ({ name, label: name, kind: "percent", unit: "%", ...extra });

describe("barLayout", () => {
  it("colours each band's bar from the colour its row carries, not from the view", () => {
    const layout = barLayout({
      fields: [field("band", { kind: "category", unit: null, colorField: "color" }), field("percent"), field("color", { kind: "category" })],
      bindings: { x: "band", y: ["percent"] },
      rows: [
        { band: "Low", percent: 2, color: "glucose-low" },
        { band: "In range", percent: 70, color: "glucose-in-range" },
        { band: "Made up", percent: 28, color: "glucose-very-high" },
      ],
    });

    expect(layout.segments.map((s) => s.color)).toEqual([
      "var(--glucose-low)",
      "var(--glucose-in-range)",
      "var(--glucose-very-high)",
    ]);
  });

  it("stacks fields in the field colours and sets each segment on the one below", () => {
    const layout = barLayout({
      fields: [
        field("date", { kind: "category", unit: null }),
        field("below", { color: "glucose-low" }),
        field("inRange", { color: "glucose-in-range" }),
        field("other"),
      ],
      bindings: { x: "date", y: ["below", "inRange", "other"], stacked: true },
      rows: [{ date: "2026-10-29", below: 2, inRange: 70, other: 28 }],
    });

    expect(layout.segments.map((s) => [s.start, s.end, s.slot, s.color])).toEqual([
      [0, 2, 0, "var(--glucose-low)"],
      [2, 72, 0, "var(--glucose-in-range)"],
      [72, 100, 0, NEUTRAL_BAR],
    ]);
  });

  it("puts side-by-side fields in their own slots from zero", () => {
    const layout = barLayout({
      fields: [field("day", { kind: "category", unit: null }), field("a"), field("b")],
      bindings: { x: "day", y: ["a", "b"] },
      rows: [{ day: "Mon", a: 3, b: null }, { day: "Tue", a: 1, b: 4 }],
    });

    expect(layout.segments.map((s) => [s.category, s.slot, s.start, s.end])).toEqual([
      ["Mon", 0, 0, 3],
      ["Tue", 0, 0, 1],
      ["Tue", 1, 0, 4],
    ]);
    expect(layout.categories).toEqual(["Mon", "Tue"]);
  });

  it("draws a value that is not a theme colour as neutral, so no row can reach another CSS variable", () => {
    const layout = barLayout({
      fields: [field("band", { kind: "category", unit: null, colorField: "color" }), field("percent"), field("color", { kind: "category" })],
      bindings: { x: "band", y: ["percent"] },
      rows: [
        { band: "A", percent: 10, color: "background); fill: url(https://example.com/x" },
        { band: "B", percent: 20, color: "primary-foreground" },
        { band: "C", percent: 30, color: null },
      ],
    });

    expect(layout.segments.map((s) => s.color)).toEqual([NEUTRAL_BAR, NEUTRAL_BAR, NEUTRAL_BAR]);
  });
});

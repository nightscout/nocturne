import { render } from "vitest-browser-svelte";
import { describe, expect, it } from "vitest";
import { ReportView } from "@nocturne/charts";
import { readReport, type ReportResult } from "@nocturne/charts/core";
import { customReportLabels } from "../custom-report-labels.svelte.js";
import { fieldSwatchOf, layerSwatchOf } from "./result-colors.js";
import pairs from "./same-colour-pairs.fixture.json";
import Swatch from "./Swatch.svelte";

// Day by day, as the engine draws it: time in range beside the ranges stack, whose in-range part is
// the same colour and mark, and total insulin beside total bolus.
const report = readReport(pairs.result) as ReportResult;
const format = { locale: "en-GB", hour12: false };

/** The bars drawn in a colour, split by whether they carry a texture. */
function barsIn(color: string): { plain: Element[]; hatched: Element[] } {
  const bars = [...document.querySelectorAll('[data-testid="report-layer-bar"]')].filter(
    (b) => b.getAttribute("fill") === `var(--${color})`
  );
  return {
    plain: bars.filter((b) => !b.hasAttribute("data-texture")),
    hatched: bars.filter((b) => b.hasAttribute("data-texture")),
  };
}

describe("two series of one colour and one mark in a lane", () => {
  it("differ by texture in the chart: time in range against the in-range part of the ranges stack", async () => {
    render(ReportView, { report, format, labels: customReportLabels() });
    await expect.poll(() => document.querySelectorAll('[data-testid="report-layer-bar"]').length).toBeGreaterThan(0);

    const { plain, hatched } = barsIn("glucose-in-range");
    expect(plain.length).toBeGreaterThan(0);
    expect(hatched.length).toBeGreaterThan(0);
    // The texture is ink over the same colour, drawn on screen and not only on paper.
    const ink = hatched[0].nextElementSibling;
    expect(ink?.getAttribute("fill")).toBe("url(#chart-ink-cat-1)");
    expect(document.querySelector("pattern#chart-ink-cat-1")).not.toBeNull();
  });

  it("total insulin and total bolus are two layers, which the engine gives two hues, so neither is hatched", async () => {
    render(ReportView, { report, format, labels: customReportLabels() });
    await expect.poll(() => document.querySelectorAll('[data-testid="report-layer-bar"]').length).toBeGreaterThan(0);

    expect(barsIn("insulin-bolus")).toMatchObject({ plain: { length: 3 }, hatched: [] });
    expect(barsIn("chart-1")).toMatchObject({ plain: { length: 3 }, hatched: [] });
  });

  it("show the same texture in the workspace's swatch of the hatched part, and none on the other", async () => {
    const hatched = fieldSwatchOf(report, "ranges", "timeInRange");
    const plain = layerSwatchOf(report, "timeInRange");
    expect(hatched).toMatchObject({ kind: "solid", color: "var(--glucose-in-range)", texture: "cat-1" });
    expect(plain).toMatchObject({ kind: "solid", color: "var(--glucose-in-range)", texture: null });

    const screen = render(Swatch, { swatch: hatched });
    const el = screen.container.querySelector('[data-texture="cat-1"]');
    expect(el).not.toBeNull();
    expect(getComputedStyle(el!).backgroundImage).toContain("gradient");
  });
});

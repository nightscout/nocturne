import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect } from "vitest";
import PairedGlucoseScatter from "./PairedGlucoseScatter.svelte";

const pairs = [
  { mgdlA: 120, mgdlB: 118 },
  { mgdlA: 90, mgdlB: 101 },
  { mgdlA: 240, mgdlB: 230 },
];

describe("PairedGlucoseScatter", () => {
  it("draws every pair as one subpath of a single mark", async () => {
    render(PairedGlucoseScatter, { props: { pairs, nameA: "Sensor A", nameB: "Sensor B" } });

    const points = page.getByTestId("paired-points");
    await expect.element(points).toBeInTheDocument();

    const d = (await points.element()).getAttribute("d") ?? "";
    expect(d.match(/M/g)?.length).toBe(pairs.length);
  });

  it("labels each axis with its device", async () => {
    render(PairedGlucoseScatter, { props: { pairs, nameA: "Sensor A", nameB: "Sensor B" } });

    await expect.element(page.getByText(/Vertical: Sensor A/)).toBeVisible();
    await expect.element(page.getByText(/Horizontal: Sensor B/)).toBeVisible();
  });

  it("draws no subpaths when there are no pairs", async () => {
    render(PairedGlucoseScatter, { props: { pairs: [], nameA: "Sensor A", nameB: "Sensor B" } });

    const d = (await page.getByTestId("paired-points").element()).getAttribute("d") ?? "";
    expect(d).toBe("");
  });
});

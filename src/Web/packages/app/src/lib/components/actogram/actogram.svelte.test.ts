import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, expect, it } from "vitest";
import Harness from "./ActogramHarness.test.svelte";

describe.each(["steps", "heart-rate"] as const)("%s actogram", (metric) => {
  it.each([
    { targetLow: 70, targetHigh: 180 },
    { targetLow: 100, targetHigh: 100 },
    { targetLow: 0, targetHigh: 0 },
  ])("renders readings with target bounds $targetLow / $targetHigh", async (targets) => {
    render(Harness, { metric, ...targets });
    const readings = page.getByTestId(
      metric === "steps" ? "step-reading" : "heart-rate-reading"
    );
    const glucose = page.getByTestId("actogram-glucose-reading");
    const limits = page.getByTestId("actogram-target-limit");

    await expect.element(readings.nth(1)).toBeInTheDocument();
    await expect.element(glucose.nth(1)).toBeInTheDocument();
    await expect.element(limits.nth(1)).toBeInTheDocument();
    expect(readings.elements()).toHaveLength(2);
    expect(glucose.elements()).toHaveLength(2);
    expect(limits.elements()).toHaveLength(2);
    for (const [index, value] of [targets.targetLow, targets.targetHigh].entries()) {
      await expect.poll(() => Number(limits.nth(index).element().getAttribute("y1")))
        .toBeCloseTo(64 * (1 - value / 300));
    }
  });
});

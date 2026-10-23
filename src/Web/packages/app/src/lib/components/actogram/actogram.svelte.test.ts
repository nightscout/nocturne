import { render } from "vitest-browser-svelte";
import { page, userEvent } from "vitest/browser";
import { describe, expect, it, vi } from "vitest";
import { createRawSnippet } from "svelte";
import Actogram from "./Actogram.svelte";
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

describe("keyboard navigation", () => {
  const days = Array.from({ length: 40 }, (_, index) => new Date(Date.UTC(2026, 8, 1 + index)));
  const row = createRawSnippet(() => ({ render: () => "<g></g>" }));

  async function press(keys: string) {
    const onVisibleRangeChange = vi.fn();
    render(Actogram, { props: { data: [], days, visibleCount: 5, onVisibleRangeChange, row } });
    await page.getByRole("toolbar").click();
    await userEvent.keyboard(keys);
    return onVisibleRangeChange;
  }

  it("moves one row per arrow key", async () => {
    const onVisibleRangeChange = await press("{ArrowDown}{ArrowDown}{ArrowUp}");

    await expect.poll(() => onVisibleRangeChange.mock.calls.length).toBe(3);
    expect(onVisibleRangeChange.mock.calls.at(-1)![0]).toEqual(days[1]);
  });

  it("moves a week with Shift", async () => {
    const onVisibleRangeChange = await press("{Shift>}{ArrowDown}{/Shift}");

    await expect.poll(() => onVisibleRangeChange.mock.calls.length).toBe(1);
    expect(onVisibleRangeChange.mock.calls[0][0]).toEqual(days[7]);
  });

  it("moves a month with Alt", async () => {
    const onVisibleRangeChange = await press("{Alt>}{ArrowDown}{/Alt}");

    await expect.poll(() => onVisibleRangeChange.mock.calls.length).toBe(1);
    expect(onVisibleRangeChange.mock.calls[0][0]).toEqual(days[30]);
  });
});

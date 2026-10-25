import { render } from "vitest-browser-svelte";
import { page, userEvent } from "vitest/browser";
import { describe, expect, it, vi } from "vitest";
import { createRawSnippet } from "svelte";
import Actogram from "./Actogram.svelte";
import type { ActogramDay } from "./actogram";
import Harness from "./ActogramHarness.test.svelte";
import { servedDay, servedDays } from "$lib/test-stubs/actogram-days";

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
  const days = servedDays("2026-09-01", 40);
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

describe("week buttons", () => {
  const newestFirst = servedDays("2026-08-02", 40).reverse();
  const row = createRawSnippet(() => ({ render: () => "<g></g>" }));

  it("Older week shows older days and Newer week returns", async () => {
    const onVisibleRangeChange = vi.fn();
    render(Actogram, { props: { data: [], days: newestFirst, visibleCount: 5, onVisibleRangeChange, row } });
    const firstVisible = () => onVisibleRangeChange.mock.calls.at(-1)![0] as ActogramDay;

    await expect.element(page.getByRole("button", { name: "Newer week" })).toBeDisabled();
    await page.getByRole("button", { name: "Older week" }).click();
    await expect.poll(() => onVisibleRangeChange.mock.calls.length).toBe(1);
    expect(firstVisible().startMills).toBeLessThan(newestFirst[0].startMills);
    expect(firstVisible()).toEqual(newestFirst[7]);

    await page.getByRole("button", { name: "Newer week" }).click();
    await expect.poll(() => onVisibleRangeChange.mock.calls.length).toBe(2);
    expect(firstVisible()).toEqual(newestFirst[0]);
  });
});

describe("hour marks on DST rows", () => {
  const row = createRawSnippet(() => ({ render: () => "<g></g>" }));
  const centreX = (element: Element) => {
    const box = element.getBoundingClientRect();
    return box.left + box.width / 2;
  };

  async function renderRow(hours: number, nextHours = 24) {
    render(Actogram, { props: { data: [], days: [servedDay("2026-03-08", 0, hours, nextHours)], row } });
    await expect.element(page.getByTestId("actogram-dim-boundary")).toBeInTheDocument();
  }

  async function expectMarksInsideTheRow() {
    const marks = page.getByTestId("actogram-hour-mark");
    await expect.element(marks.nth(7)).toBeInTheDocument();
    expect(marks.elements()).toHaveLength(8);
    const boundary = page.getByTestId("actogram-dim-boundary").element().getBoundingClientRect();
    const chart = page.getByTestId("actogram-row").element().querySelector("svg")!.getBoundingClientRect();
    for (const mark of marks.elements()) {
      const box = mark.getBoundingClientRect();
      expect(box.left).toBeGreaterThanOrEqual(chart.left - 0.5);
      expect(box.right).toBeLessThanOrEqual(chart.right + 0.5);
    }
    return { marks, boundary };
  }

  it.each([
    { name: "DST start", hours: 23 },
    { name: "DST end", hours: 25 },
  ])("puts the second midnight label on the dim boundary at $name", async ({ hours }) => {
    await renderRow(hours);
    const { marks, boundary } = await expectMarksInsideTheRow();

    const midnight = marks.nth(4).element();
    expect(midnight.textContent?.trim()).toBe("0h");
    expect(centreX(midnight)).toBeCloseTo(boundary.left, 0);
  });

  it.each([
    { name: "DST start", nextHours: 23 },
    { name: "DST end", nextHours: 25 },
  ])("keeps every label inside the row of the day before $name", async ({ nextHours }) => {
    await renderRow(24, nextHours);
    const { marks, boundary } = await expectMarksInsideTheRow();

    expect(centreX(marks.nth(4).element())).toBeCloseTo(boundary.left, 0);
  });

  it("draws no row labels on a 24 hour day", async () => {
    await renderRow(24);

    expect(page.getByTestId("actogram-hour-mark").elements()).toHaveLength(0);
  });
});

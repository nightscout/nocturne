import { render } from "vitest-browser-svelte";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { TreatmentFood } from "$lib/api";
import CarbBreakdownBar from "./CarbBreakdownBar.svelte";

const food = (id: string, carbs: number) => ({ id, foodName: id, carbs }) as unknown as TreatmentFood;

const wash = (container: HTMLElement) => container.querySelector<HTMLElement>("[data-carb-wash]");


afterEach(() => vi.restoreAllMocks());

describe("CarbBreakdownBar wash", () => {
  it("coalesces chart mutation bursts and follows the final attributed extent", async () => {
    const { container } = render(CarbBreakdownBar, { totalCarbs: 40, foods: [food("oats", 10)] });
    await expect.poll(() => wash(container)).not.toBeNull();
    const attributed = [...container.querySelectorAll("rect")].find(rect => rect.getAttribute("fill") === "oklch(0.765 0.177 163.223)")!;
    const read_bounds = vi.spyOn(attributed, "getBoundingClientRect");
    await new Promise<void>(resolve => requestAnimationFrame(() => resolve()));
    read_bounds.mockClear();
    for (let width = 1; width <= 20; width++) {
      attributed.setAttribute("width", String(width));
      await Promise.resolve();
    }
    await new Promise<void>(resolve => requestAnimationFrame(() => resolve()));
    await new Promise<void>(resolve => requestAnimationFrame(() => resolve()));
    expect(read_bounds.mock.calls.length).toBe(1);
    const final_bounds = attributed.getBoundingClientRect();
    await expect.poll(() => Math.abs(wash(container)!.getBoundingClientRect().width - final_bounds.width)).toBeLessThan(1);
  });
  it("keeps the wash backing canvas inside the attributed extent", async () => {
    const { container } = render(CarbBreakdownBar, { totalCarbs: 40, foods: [food("oats", 10), food("milk", 5)] });
    await expect.poll(() => {
      const painted = wash(container);
      const canvas = painted?.querySelector("canvas");
      if (!painted || !canvas) return false;
      const bounds = painted.getBoundingClientRect();
      const dpr = Math.min(2, devicePixelRatio);
      return bounds.width > 0 && Math.abs(canvas.width - Math.round(bounds.width * dpr)) <= 1
        && Math.abs(canvas.height - Math.round(bounds.height * dpr)) <= 1;
    }).toBe(true);
  });

  it("covers exactly the attributed bars", async () => {
    const { container } = render(CarbBreakdownBar, { totalCarbs: 40, foods: [food("oats", 10), food("milk", 5)] });

    await expect.poll(() => wash(container)).not.toBeNull();
    const attributed = [...container.querySelectorAll("rect")].filter(
      (rect) => rect.getAttribute("fill") !== "oklch(0.556 0.046 257.417)" && rect.getBoundingClientRect().width > 0
    );
    const left = Math.min(...attributed.map((r) => r.getBoundingClientRect().left));
    const right = Math.max(...attributed.map((r) => r.getBoundingClientRect().right));
    await expect
      .poll(() => {
        const painted = wash(container)!.getBoundingClientRect();
        return Math.abs(painted.left - left) < 1 && Math.abs(painted.right - right) < 1;
      })
      .toBe(true);
  });

  it("paints no wash when nothing is attributed", async () => {
    const { container } = render(CarbBreakdownBar, { totalCarbs: 40, foods: [] });

    await expect.poll(() => container.querySelectorAll("rect").length).toBeGreaterThan(0);
    expect(wash(container)).toBeNull();
  });
});

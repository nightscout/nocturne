import { describe, expect, it, vi } from "vitest";
import { render } from "vitest-browser-svelte";
import type { SleepNightSummary } from "#lib/api/index.js";
const { goto } = vi.hoisted(() => ({ goto: vi.fn() }));
vi.mock("$app/navigation", () => ({ goto }));
vi.mock("$app/paths", () => ({
  resolve: (id: string, params: Record<string, string>) => id.replace("[date]", params.date),
}));

import SleepCompositionChart from "./SleepCompositionChart.svelte";

function night(displayDate: string): SleepNightSummary {
  return {
    sessionId: `session-${displayDate}`,
    displayDate,
    deepMinutes: 60,
    remMinutes: 90,
    lightMinutes: 240,
    awakeMinutes: 20,
    sleepMinutes: 390,
  };
}

function dayColumns(container: HTMLElement): SVGRectElement[] {
  return Array.from(container.querySelectorAll<SVGRectElement>("rect[fill='transparent']"));
}

describe("SleepCompositionChart", () => {
  it("draws one column per served day, gap days included", async () => {
    const days = ["2026-01-10", "2026-01-11", "2026-01-12"];

    const { container } = render(SleepCompositionChart, {
      nights: [night("2026-01-10"), night("2026-01-12")],
      days,
    });

    await expect.poll(() => dayColumns(container).length).toBe(days.length);
    const linked = dayColumns(container).map((column) => column.getAttribute("role") === "link");
    expect(linked).toEqual([true, false, true]);
  });

  it("drops a night whose display day is not in the served list", async () => {
    const { container } = render(SleepCompositionChart, {
      nights: [night("2026-01-09"), night("2026-01-10")],
      days: ["2026-01-10", "2026-01-11"],
    });

    await expect.poll(() => dayColumns(container).length).toBe(2);
    const linked = dayColumns(container).map((column) => column.getAttribute("role") === "link");
    expect(linked).toEqual([true, false]);
  });

  it("takes focus, click and Enter only on days with a night", async () => {
    goto.mockClear();
    const { container } = render(SleepCompositionChart, {
      nights: [night("2026-01-10"), night("2026-01-12")],
      days: ["2026-01-10", "2026-01-11", "2026-01-12"],
    });
    await expect.poll(() => dayColumns(container).length).toBe(3);
    const [first, gap, last] = dayColumns(container);

    expect(first.getAttribute("tabindex")).toBe("0");
    expect(gap.getAttribute("tabindex")).toBeNull();
    expect(first.getAttribute("aria-label")).toContain("Sleep session on");

    first.dispatchEvent(new MouseEvent("click", { bubbles: true }));
    expect(goto).toHaveBeenCalledTimes(1);
    expect(String(goto.mock.calls[0][0])).toContain("2026-01-10");

    last.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true }));
    expect(goto).toHaveBeenCalledTimes(2);
    expect(String(goto.mock.calls[1][0])).toContain("2026-01-12");

    gap.dispatchEvent(new MouseEvent("click", { bubbles: true }));
    gap.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true }));
    expect(goto).toHaveBeenCalledTimes(2);
  });
});

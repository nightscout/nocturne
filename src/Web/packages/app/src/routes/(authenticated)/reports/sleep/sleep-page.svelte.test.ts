import { render } from "vitest-browser-svelte";
import { describe, expect, it, vi } from "vitest";

const hoisted = vi.hoisted(() => ({ trends: {} as Record<string, unknown> }));

vi.mock("$lib/hooks/actogram-report.svelte", () => ({
  useActogramReport: () => ({
    params: {
      dateRangeMillis: { from: new Date(2026, 0, 1).getTime(), to: new Date(2026, 0, 1).getTime() },
      dateRangeInput: {},
      dayCount: 1,
    },
    resource: { current: { sleepSpans: [], glucoseData: [] } },
    days: [new Date(2026, 0, 1)],
    rangeDayCount: 1,
  }),
}));
vi.mock("$api/reports.remote", () => ({ getSleepTrends: vi.fn() }));
vi.mock("$lib/hooks/resource-context.svelte", () => ({
  contextResource: () => ({
    get current() {
      return hoisted.trends;
    },
    loading: false,
    error: null,
  }),
}));
vi.mock("runed/kit", () => ({
  useSearchParams: () => ({ source: null }),
}));

import SleepPage from "./+page.svelte";

function compositionColumns(container: HTMLElement): SVGRectElement[] {
  const heading = Array.from(container.querySelectorAll("[data-slot='card-title']")).find((el) =>
    el.textContent?.includes("Sleep composition")
  );
  const card = heading?.closest("[data-slot='card']");
  return Array.from(card?.querySelectorAll<SVGRectElement>("rect[fill='transparent']") ?? []);
}

describe("sleep report page", () => {
  it("draws the composition chart over the days the trends report served", async () => {
    hoisted.trends = {
      days: ["2026-01-10", "2026-01-11", "2026-01-12"],
      nights: [
        {
          sessionId: "a",
          displayDate: "2026-01-10",
          deepMinutes: 60,
          remMinutes: 90,
          lightMinutes: 240,
          sleepMinutes: 390,
        },
      ],
      weeks: [],
      summary: { nightCount: 1 },
    };

    const { container } = render(SleepPage);

    await expect.poll(() => compositionColumns(container).length).toBe(3);
  });
});

import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { TimeZoneUnavailableReason } from "#lib/api/index.js";
import { describe, expect, it, vi } from "vitest";
import { servedDays } from "#lib/test-stubs/actogram-days.js";

const CHATHAM_MINUTES = 13 * 60 + 45;

const hoisted = vi.hoisted(() => ({ trends: {} as Record<string, unknown> }));

vi.mock("#lib/hooks/actogram-report.svelte.js", () => ({
  useActogramReport: () => ({
    params: {
      dateRangeMillis: { from: 0, to: 0 },
      dateRangeInput: {},
      dayCount: 1,
    },
    resource: { current: { sleepSpans: [], glucoseData: [] } },
    days: servedDays("2026-01-10", 3, CHATHAM_MINUTES).reverse(),
    rangeDayCount: 3,
  }),
}));
vi.mock("#lib/api/reports.remote.js", () => ({ getSleepTrends: vi.fn() }));
vi.mock("#lib/hooks/resource-context.svelte.js", () => ({
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

  it("links each actogram row to the night the composition chart shows for that day", async () => {
    const night = (displayDate: string) => ({
      sessionId: displayDate,
      displayDate,
      inBedAt: new Date(`${displayDate}T03:00:00Z`),
      deepMinutes: 60,
      remMinutes: 90,
      lightMinutes: 240,
      sleepMinutes: 390,
    });
    hoisted.trends = {
      days: ["2026-01-10", "2026-01-11", "2026-01-12"],
      nights: [night("2026-01-10"), night("2026-01-12")],
      weeks: [],
      summary: { nightCount: 2 },
    };

    const { container } = render(SleepPage);

    await expect.poll(() => compositionColumns(container).length).toBe(3);
    const chartLinks = compositionColumns(container).map((column) => column.getAttribute("role") === "link");
    expect(chartLinks).toEqual([true, false, true]);

    const rows = Array.from(container.querySelectorAll<HTMLElement>("[data-testid='actogram-row']"));
    expect(rows.map((row) => row.dataset.day)).toEqual(["2026-01-12", "2026-01-11", "2026-01-10"]);
    const rowLinked = rows.map((row) => row.querySelector("a") !== null);
    expect(rowLinked).toEqual([...chartLinks].reverse());
  });

  const NOTE = /Days in range, the weekly breakdown, the composition chart and the actogram rows\s+use UTC days/;

  it("says why the days are UTC days when the trends report carries a reason", async () => {
    hoisted.trends = {
      days: ["2026-01-10"],
      nights: [{
          sessionId: "a",
          displayDate: "2026-01-10",
          deepMinutes: 60,
          remMinutes: 90,
          lightMinutes: 240,
          sleepMinutes: 390,
        }],
      weeks: [],
      summary: { nightCount: 1 },
      timeZoneUnavailableReason: TimeZoneUnavailableReason.NotConfigured,
    };

    render(SleepPage);

    await expect.element(page.getByText(NOTE)).toBeVisible();
    await expect.element(page.getByText(/do not have a time zone/)).toBeVisible();
  });

  it("shows no note when the trends report carries no reason", async () => {
    hoisted.trends = {
      days: ["2026-01-10"],
      nights: [{
          sessionId: "a",
          displayDate: "2026-01-10",
          deepMinutes: 60,
          remMinutes: 90,
          lightMinutes: 240,
          sleepMinutes: 390,
        }],
      weeks: [],
      summary: { nightCount: 1 },
    };

    render(SleepPage);

    await expect.element(page.getByText("Nights tracked")).toBeVisible();
    await expect.element(page.getByText(NOTE)).not.toBeInTheDocument();
  });
});

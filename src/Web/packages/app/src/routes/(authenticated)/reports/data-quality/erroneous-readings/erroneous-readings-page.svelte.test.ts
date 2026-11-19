import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, expect, it, vi } from "vitest";
import {
  ErroneousEpisodeKind,
  type ErroneousReadingEpisode,
  type ErroneousReadingsDay,
} from "#lib/api/index.js";
import { PRINT_LAYOUT_CLASS } from "#lib/components/charts/print/print-mode.svelte.js";

const resource = vi.hoisted(() => ({ current: null as unknown }));

vi.mock("#lib/api/reports.remote.js", () => ({ getErroneousReadingsReport: vi.fn() }));
vi.mock("#lib/hooks/date-params.svelte.js", () => ({
  requireDateParamsContext: () => ({ dateRangeInput: {} }),
}));
vi.mock("#lib/hooks/resource-context.svelte.js", () => ({
  contextResource: () => resource,
}));
// The dashboard chart needs the realtime store and the chart-data endpoint; it has its own tests.
vi.mock("#lib/components/reports/ReportGlucoseChart.svelte", async () => ({
  default: (await import("#lib/test-stubs/Empty.test-stub.svelte")).default,
}));

import ErroneousReadingsPage from "./+page.svelte";

const minute = 60_000;
const monday = Date.UTC(2026, 9, 19, 13, 0);
const thursday = Date.UTC(2026, 9, 22, 8, 0);

const episode = (start: number, invalid: number, low: number, high: number): ErroneousReadingEpisode => ({
  kind: ErroneousEpisodeKind.Readings,
  startMills: start,
  endMills: start + (invalid - 1) * 5 * minute,
  source: "xDrip",
  invalidCount: invalid,
  uncertainCount: 0,
  excludedMinutes: invalid * 5,
  minMgdl: low,
  maxMgdl: high,
  sensorStatuses: ["G7:Errors"],
  meterReadings: [{ mills: start + 10 * minute, mgdl: 180 }],
});

/** A UTC day, as the API cuts it for a patient with no zone. */
const day = (date: string, episodes: ErroneousReadingEpisode[], isolated: number[] = []): ErroneousReadingsDay => {
  const from = Date.parse(`${date}T00:00:00Z`);
  return {
    date,
    fromMills: from,
    toMills: from + 24 * 60 * minute,
    episodes,
    isolatedReadingMills: isolated,
    invalidReadings: episodes.reduce((n, e) => n + (e.invalidCount ?? 0), 0) + isolated.length,
  };
};

/** Newest day first, as the API sends them. */
const show = (days: ErroneousReadingsDay[]) => {
  const invalid = days.reduce((n, d) => n + (d.invalidReadings ?? 0), 0);
  resource.current = {
    days,
    summary: {
      episodes: days.reduce((n, d) => n + (d.episodes?.length ?? 0), 0),
      invalidReadings: invalid,
      isolatedReadings: days.reduce((n, d) => n + (d.isolatedReadingMills?.length ?? 0), 0),
      uncertainReadings: 0,
      excludedMinutes: invalid * 5,
    },
  };
};

describe("erroneous readings page", () => {
  it("says so when the sensor marked nothing as not reliable", async () => {
    show([]);

    render(ErroneousReadingsPage);

    await expect.element(page.getByText(/did not mark a reading as not reliable/)).toBeVisible();
  });

  it("shows a day with only single invalid readings, which make no episode", async () => {
    show([day("2026-10-19", [], [monday, monday + 60 * minute, monday + 120 * minute])]);

    render(ErroneousReadingsPage);

    await expect.element(page.getByText("3 on their own", { exact: true })).toBeVisible();
    await expect.element(page.getByText("3 invalid readings · 3 on their own")).toBeVisible();
    await expect.element(page.getByText("Not counted", { exact: true })).not.toBeInTheDocument();
  });

  it("shows one card per day, newest first, with that day's episodes", async () => {
    show([
      day("2026-10-22", [episode(thursday, 8, 200, 260)]),
      day("2026-10-19", [episode(monday, 17, 45, 88)], [monday + 300 * minute]),
    ]);

    render(ErroneousReadingsPage);

    const cards = page.getByText(/^(Monday|Thursday)/);
    await expect.element(cards.first()).toHaveTextContent(/Thursday/);
    await expect.element(cards.nth(1)).toHaveTextContent(/Monday/);
    await expect.element(page.getByText("1 episode · 8 invalid readings")).toBeVisible();
    await expect.element(page.getByText("1 episode · 18 invalid readings · 1 on its own")).toBeVisible();
    await expect.element(page.getByText("200–260", { exact: true })).toBeVisible();
    await expect.element(page.getByText("45–88", { exact: true })).toBeVisible();
    await expect.element(page.getByText("Sensor error, not counted").first()).toBeVisible();
  });

  it("draws one day's chart at a time, the newest first", async () => {
    show([day("2026-10-22", [episode(thursday, 8, 200, 260)]), day("2026-10-19", [episode(monday, 17, 45, 88)])]);

    render(ErroneousReadingsPage);

    const legend = page.getByText("Sensor error, not counted");
    const showChart = page.getByRole("button", { name: "Show chart" });
    await expect.element(legend.first()).toBeVisible();
    await expect.element(legend.nth(1)).not.toBeInTheDocument();
    await expect.element(showChart.nth(1)).not.toBeInTheDocument();

    await showChart.click();

    await expect.element(legend.nth(1)).not.toBeInTheDocument();
    await expect.element(showChart).toBeVisible();
    await expect.element(page.getByText(/^Thursday/)).toBeVisible();
  });

  it("draws every day's chart on paper, where there is no 'Show chart'", async () => {
    show([day("2026-10-22", [episode(thursday, 8, 200, 260)]), day("2026-10-19", [episode(monday, 17, 45, 88)])]);
    document.documentElement.classList.add(PRINT_LAYOUT_CLASS);
    try {
      render(ErroneousReadingsPage);

      const legend = page.getByText("Sensor error, not counted");
      await expect.element(legend.nth(1)).toBeVisible();
      await expect.element(page.getByRole("button", { name: "Show chart" })).not.toBeInTheDocument();
    } finally {
      document.documentElement.classList.remove(PRINT_LAYOUT_CLASS);
    }
  });
});

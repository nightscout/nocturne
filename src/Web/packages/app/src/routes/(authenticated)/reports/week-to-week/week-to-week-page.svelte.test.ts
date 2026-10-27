import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { TimeZoneUnavailableReason } from "#lib/api/index.js";

const weekday = vi.hoisted(() => ({ current: undefined as unknown }));

vi.mock("layerchart", async () => ({
  LineChart: (await import("#lib/components/reports/LineChartStub.test.svelte")).default,
}));
vi.mock("#lib/api/reports.remote.js", () => ({ getWeekdayAverages: vi.fn() }));
vi.mock("#lib/hooks/date-params.svelte.js", () => ({
  requireDateParamsContext: () => ({
    dateRangeInput: {},
    startDate: new Date(2026, 2, 1),
    endDate: new Date(2026, 2, 7),
    fromDay: "2026-03-01",
    toDay: "2026-03-07",
    isDefault: true,
    setCustomRange: () => {},
    reset: () => {},
  }),
}));
vi.mock("#lib/hooks/resource-context.svelte.js", () => ({
  contextResource: () => ({
    get current() {
      return weekday.current;
    },
    loading: false,
    error: null,
  }),
}));

import WeekToWeekPage from "./+page.svelte";

const NOTE = /cannot average glucose by weekday and time of day/;
const EMPTY = "No data available for this week";

describe("week-to-week report", () => {
  beforeEach(() => {
    weekday.current = undefined;
  });

  it("shows the time zone note in place of an empty chart when the response carries a reason", async () => {
    weekday.current = {
      slots: [],
      timeZoneUnavailableReason: TimeZoneUnavailableReason.NotConfigured,
    };
    render(WeekToWeekPage);

    await expect.element(page.getByText(NOTE)).toBeVisible();
    await expect.element(page.getByText(EMPTY)).not.toBeInTheDocument();
  });

  it("shows the plain empty state when there are no slots and no reason", async () => {
    weekday.current = { slots: [] };
    render(WeekToWeekPage);

    await expect.element(page.getByText(EMPTY)).toBeVisible();
    await expect.element(page.getByText(NOTE)).not.toBeInTheDocument();
  });

  it("draws the slots of the envelope without a note", async () => {
    weekday.current = {
      slots: [
        { minuteOfDay: 420, mean: { Monday: 110 } },
        { minuteOfDay: 425, mean: { Monday: 120 } },
      ],
    };
    render(WeekToWeekPage);

    await expect.element(page.getByTestId("chart-rows")).toHaveTextContent("2");
    await expect.element(page.getByText(EMPTY)).not.toBeInTheDocument();
    await expect.element(page.getByText(NOTE)).not.toBeInTheDocument();
  });
});

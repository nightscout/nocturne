import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, expect, it, vi } from "vitest";
import { TimeZoneUnavailableReason } from "#lib/api/index.js";
import { servedDay, servedDays } from "#lib/test-stubs/actogram-days.js";

const CHATHAM_MINUTES = 13 * 60 + 45;
const utcDay = servedDay("2025-01-10");

const hoisted = vi.hoisted(() => ({
  current: {} as Record<string, unknown>,
  days: [] as unknown[],
}));

vi.mock("#lib/hooks/actogram-report.svelte.js", () => ({
  useActogramReport: () => ({
    params: {
      dateRangeMillis: { from: 0, to: 0 },
      dayCount: 1,
    },
    resource: {
      get current() {
        return hoisted.current;
      },
    },
    get days() {
      return hoisted.days;
    },
    rangeDayCount: 1,
  }),
}));

import StepsPage from "./+page.svelte";

function respond(timeZoneUnavailableReason?: TimeZoneUnavailableReason) {
  hoisted.days = [utcDay];
  hoisted.current = {
    stepCounts: [{ mills: utcDay.startMills + 12 * 3_600_000, metric: 4321 }],
    stepDayTotals: { "2025-01-10": 4321 },
    glucoseData: [],
    timeZoneUnavailableReason,
  };
}

function rowOf(date: string): HTMLElement {
  return document.querySelector<HTMLElement>(`[data-testid='actogram-row'][data-day='${date}']`)!;
}

function barsIn(row: HTMLElement, extended: boolean): number[] {
  return Array.from(row.querySelectorAll<SVGRectElement>("[data-testid='step-reading']"))
    .filter((bar) => (bar.dataset.extended === "true") === extended)
    .map((bar) => Number(bar.dataset.steps));
}

function totalOf(row: HTMLElement): number {
  const text = row.querySelector("[data-testid='day-total']")!.textContent!;
  return Number(text.replace(/[^0-9]/g, ""));
}

describe("steps report", () => {
  it("shows the day total on each row when the time zone resolved", async () => {
    respond();
    render(StepsPage);

    await expect.element(page.getByTestId("step-reading").first()).toBeInTheDocument();
    await expect.element(page.getByTestId("day-total")).toHaveTextContent(/4,321\s*steps/);
    await expect.element(page.getByText(/use\s+UTC days/)).not.toBeInTheDocument();
  });

  it.each([
    [TimeZoneUnavailableReason.Share, /A shared link does not show the time zone/],
    [TimeZoneUnavailableReason.NotConfigured, /do not have a time zone/],
    [TimeZoneUnavailableReason.Unrecognised, /does not recognize the time zone/],
    [TimeZoneUnavailableReason.LookupFailed, /did not load/],
  ])("keeps the bars and row totals and says the days are UTC days for %s", async (reason, sentence) => {
    respond(reason);
    render(StepsPage);

    await expect.element(page.getByText(sentence)).toBeVisible();
    await expect.element(page.getByText(/The rows and their step totals use\s+UTC days/)).toBeVisible();
    await expect.element(page.getByTestId("step-reading").first()).toBeInTheDocument();
    await expect.element(page.getByText("Total steps", { exact: true })).toBeVisible();
    await expect.element(page.getByTestId("day-total")).toHaveTextContent(/4,321\s*steps/);
  });

  describe("with a patient zone far from the browser's", () => {
    const days = servedDays("2025-01-10", 2, CHATHAM_MINUTES);
    const [first, second] = days;
    const samples = [
      { mills: first.startMills - 1, metric: 7 },
      { mills: first.startMills, metric: 100 },
      { mills: first.startMills + 6 * 3_600_000, metric: 200 },
      { mills: first.endMills - 1, metric: 300 },
      { mills: first.endMills, metric: 1000 },
      { mills: second.endMills - 1, metric: 2000 },
    ];

    function respondInChatham() {
      hoisted.days = days;
      hoisted.current = {
        stepCounts: samples.map((s) => ({ mills: s.mills, metric: s.metric })),
        stepDayTotals: Object.fromEntries(
          days.map((day) => [
            day.date,
            samples
              .filter((s) => s.mills >= day.startMills && s.mills < day.endMills)
              .reduce((sum, s) => sum + s.metric, 0),
          ])
        ),
        glucoseData: [],
      };
    }

    it("makes each row's total equal the bars in that row", async () => {
      respondInChatham();
      render(StepsPage);

      await expect.element(page.getByTestId("day-total").first()).toBeInTheDocument();
      for (const day of days) {
        const row = rowOf(day.date);
        const sum = barsIn(row, false).reduce((a, b) => a + b, 0);
        expect(totalOf(row)).toBe(sum);
      }
    });

    it("puts the edge samples in the rows the served days put them in", async () => {
      respondInChatham();
      render(StepsPage);

      await expect.element(page.getByTestId("day-total").first()).toBeInTheDocument();
      expect(barsIn(rowOf(first.date), false)).toEqual([100, 200, 300]);
      expect(barsIn(rowOf(second.date), false)).toEqual([1000, 2000]);
      expect(barsIn(rowOf(first.date), true)).toEqual([1000, 2000]);
    });
  });
});

import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, expect, it, vi } from "vitest";
import { TimeZoneUnavailableReason } from "$lib/api";

const day = new Date(2025, 0, 10);
const noon = new Date(2025, 0, 10, 12).getTime();

const hoisted = vi.hoisted(() => ({ current: {} as Record<string, unknown> }));

vi.mock("$lib/hooks/actogram-report.svelte", () => ({
  useActogramReport: () => ({
    params: {
      dateRangeMillis: { from: new Date(2025, 0, 10).getTime(), to: new Date(2025, 0, 11).getTime() },
      dayCount: 1,
    },
    resource: {
      get current() {
        return hoisted.current;
      },
    },
    days: [day],
    rangeDayCount: 1,
  }),
}));

import StepsPage from "./+page.svelte";

function respond(timeZoneUnavailableReason?: TimeZoneUnavailableReason) {
  hoisted.current = {
    stepCounts: [{ mills: noon, metric: 4321 }],
    stepDayTotals: timeZoneUnavailableReason ? {} : { "2025-01-10": 4321 },
    glucoseData: [],
    timeZoneUnavailableReason,
  };
}

describe("steps report", () => {
  it("shows the day total on each row when the time zone resolved", async () => {
    respond();
    render(StepsPage);

    await expect.element(page.getByTestId("step-reading").first()).toBeInTheDocument();
    await expect.element(page.getByTestId("day-total")).toHaveTextContent(/4,321\s*steps/);
    await expect.element(page.getByText(/cannot calculate the total steps/)).not.toBeInTheDocument();
  });

  it.each([
    [TimeZoneUnavailableReason.Share, /A shared link does not show the time zone/],
    [TimeZoneUnavailableReason.NotConfigured, /do not have a time zone/],
    [TimeZoneUnavailableReason.Unrecognised, /does not recognize the time zone/],
    [TimeZoneUnavailableReason.LookupFailed, /did not load/],
  ])("keeps the bars and total steps but drops the row total for %s", async (reason, sentence) => {
    respond(reason);
    render(StepsPage);

    await expect.element(page.getByText(sentence)).toBeVisible();
    await expect.element(page.getByText(/cannot calculate the total steps for each local day/)).toBeVisible();
    await expect.element(page.getByTestId("step-reading").first()).toBeInTheDocument();
    await expect.element(page.getByText("Total steps", { exact: true })).toBeVisible();
    await expect.element(page.getByTestId("day-total")).not.toBeInTheDocument();
  });
});

import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, expect, it, vi } from "vitest";
import { remoteCommand, remoteQuery } from "#lib/test-stubs/remote-resource.js";

// The page opens on the current month, so the data is dated in it.
const now = new Date();
const year = now.getFullYear();
const month = now.getMonth();
const date = `${year}-${String(month + 1).padStart(2, "0")}-01`;

const punchCard = {
  months: [
    {
      year,
      month,
      days: [
        {
          date,
          timestamp: new Date(year, month, 1).getTime(),
          totalReadings: 288,
          inRangePercent: 80,
          lowPercent: 5,
          highPercent: 15,
          entries: [],
        },
      ],
      summary: { dayCount: 1, totalReadings: 288, inRangePercent: 80, avgGlucose: 140 },
    },
  ],
};

vi.mock("#lib/api/generated/statistics.generated.remote.js", () => ({
  getPunchCardData: () => remoteQuery(() => punchCard),
}));

vi.mock("#lib/api/generated/trackers.generated.remote.js", () => ({
  getDefinitions: () => remoteQuery(() => []),
  getActiveInstances: () => remoteQuery(() => []),
  getInstanceHistory: () => remoteQuery(() => []),
  completeInstance: remoteCommand(() => Promise.resolve()),
}));

vi.mock("@nocturne/coach", () => ({ coachmark: () => () => {} }));

// The app layout supplies the tooltip provider the day cells need.
import CalendarPage from "./CalendarPageHarness.test.svelte";

describe("calendar page", () => {
  it("shows the month it opens on without a change of month", async () => {
    render(CalendarPage, {});

    await expect.element(page.getByText("Avg TIR")).toBeVisible();
    await expect.element(page.getByText("80.0%")).toBeVisible();
  });
});

import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, afterEach } from "vitest";
import type { ActiveProfileSchedules } from "$lib/api/generated/nocturne-api-client";
import { timeFormat } from "$lib/stores/appearance-store.svelte";
import ScheduleFooter from "./ScheduleFooter.svelte";

const profile = (
  profileName: string,
  share: number,
  carbRatio: number,
): ActiveProfileSchedules => ({
  profileName,
  share,
  carbRatio: [
    { time: "00:00", timeAsSeconds: 0, value: carbRatio },
    { time: "13:30", timeAsSeconds: 48600, value: carbRatio + 2 },
  ],
});

describe("ScheduleFooter", () => {
  afterEach(() => {
    timeFormat.current = "12";
  });

  // Both directions are asserted because whichever one matches the browser's own
  // locale would pass without the preference being consulted at all.
  it.each([
    { format: "24" as const, shown: /^13:30: 12 g\/U$/, hidden: /1:30/, axis: "18" },
    { format: "12" as const, shown: /^1:30\s?PM: 12 g\/U$/i, hidden: /13:30/, axis: /6\s?PM/i },
  ])("honours the $format-hour preference", async ({ format, shown, hidden, axis }) => {
    timeFormat.current = format;
    render(ScheduleFooter, { profiles: [profile("Default", 1, 10)] });

    await expect.element(page.getByTitle(shown)).toBeInTheDocument();
    await expect.element(page.getByTitle(hidden)).not.toBeInTheDocument();
    await expect.element(page.getByText(axis, { exact: true })).toBeVisible();
  });

  it("names no profile when the window read only one", async () => {
    render(ScheduleFooter, { profiles: [profile("Default", 1, 10)] });

    await expect.element(page.getByText("Default")).not.toBeInTheDocument();
  });

  it("gives each profile its own section, with its name and share", async () => {
    render(ScheduleFooter, {
      profiles: [profile("Default", 0.86, 10), profile("Exercise", 0.14, 20)],
    });

    await expect.element(page.getByText(/Default\s*·\s*86%/)).toBeVisible();
    await expect.element(page.getByText(/Exercise\s*·\s*14%/)).toBeVisible();
    await expect.element(page.getByText("10", { exact: true })).toBeVisible();
    await expect.element(page.getByText("20", { exact: true })).toBeVisible();
  });

  it("shows a share that rounds to nothing as under one percent", async () => {
    render(ScheduleFooter, {
      profiles: [profile("Default", 0.998, 10), profile("Exercise", 0.002, 20)],
    });

    await expect.element(page.getByText(/Exercise\s*·\s*<1%/)).toBeVisible();
  });

  it.each([
    { applied: true, wording: /at 80%.*already in the schedules shown/ },
    { applied: false, wording: /at 80%.*not applied to the schedules shown/ },
  ])("says whether the percentage is in the schedules (applied: $applied)", async ({ applied, wording }) => {
    render(ScheduleFooter, {
      profiles: [{ ...profile("Default", 1, 10), percentage: 80, adjustmentApplied: applied }],
    });

    await expect.element(page.getByText(wording)).toBeVisible();
  });
});

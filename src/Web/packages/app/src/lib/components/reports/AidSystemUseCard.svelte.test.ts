import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, expect, it } from "vitest";
import { TimeZoneUnavailableReason, type AidSystemMetrics } from "$lib/api";
import AidSystemUseCard from "./AidSystemUseCard.svelte";

const metrics = {
  cgmDeviceNames: "Dexcom G7",
  pumpDeviceNames: "Omnipod 5",
  cgmActivePercent: 91.4,
  aidActivePercent: 84.2,
  siteChangeCount: 5,
} as AidSystemMetrics;

describe("AidSystemUseCard", () => {
  it("shows the metrics and no note when the time zone resolved", async () => {
    render(AidSystemUseCard, { props: { metrics } });

    await expect.element(page.getByText("Dexcom G7")).toBeVisible();
    await expect.element(page.getByText(/UTC days/)).not.toBeInTheDocument();
  });

  it.each([
    [TimeZoneUnavailableReason.NotConfigured, /do not have a time zone/],
    [TimeZoneUnavailableReason.Share, /A shared link does not show the time zone/],
    [TimeZoneUnavailableReason.LookupFailed, /The time zone of this data did not load/],
    [TimeZoneUnavailableReason.Unrecognised, /does not recognise the time zone/],
  ])("shows the metrics and the UTC note for %s", async (reason, cause) => {
    render(AidSystemUseCard, { props: { metrics: { ...metrics, timeZoneUnavailableReason: reason } as AidSystemMetrics } });

    await expect.element(page.getByText("Dexcom G7")).toBeVisible();
    await expect.element(page.getByText("Omnipod 5")).toBeVisible();
    await expect.element(page.getByText("91%")).toBeVisible();
    await expect.element(page.getByText("5", { exact: true })).toBeVisible();
    await expect.element(page.getByText(cause)).toBeVisible();
    await expect.element(page.getByText(/counts device use on UTC days/)).toBeVisible();
  });
});

import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, expect, it } from "vitest";
import { TimeZoneUnavailableReason } from "$lib/api";
import ReliabilityBadge from "./ReliabilityBadge.svelte";

const reliability = { meetsReliabilityCriteria: false, daysOfData: 3, recommendedMinimumDays: 14 };

describe("ReliabilityBadge", () => {
  it("says the days are UTC days when the tenant has no time zone", async () => {
    render(ReliabilityBadge, {
      props: { reliability, timeZoneUnavailableReason: TimeZoneUnavailableReason.Unrecognised },
    });

    await expect.element(page.getByText(/Based on 3 days of data/)).toBeVisible();
    await expect.element(page.getByText("Days of data are counted on UTC days.")).toBeVisible();
    await expect.element(page.getByText(/does not recognize the time zone/)).toBeVisible();
  });

  it("says nothing about time zones when a zone applied", async () => {
    render(ReliabilityBadge, { props: { reliability } });

    await expect.element(page.getByText(/Based on 3 days of data/)).toBeVisible();
    await expect.element(page.getByText(/UTC days/)).not.toBeInTheDocument();
  });
});

import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { TimeZoneUnavailableReason } from "#lib/api/index.js";

const resource = vi.hoisted(() => ({ current: undefined as unknown }));

vi.mock("#lib/api/idp.remote.js", () => ({ getIdpData: vi.fn() }));
vi.mock("#lib/hooks/date-params.svelte.js", () => ({
  requireDateParamsContext: () => ({ dateRangeInput: {} }),
}));
vi.mock("#lib/hooks/resource-context.svelte.js", () => ({
  contextResource: () => ({
    get current() {
      return resource.current;
    },
    date: { from: new Date(2026, 2, 1), to: new Date(2026, 2, 14), dayCount: 14 },
    loading: false,
    error: null,
    refresh: () => {},
  }),
}));
vi.mock("#lib/components/reports/TIRStackedChart.svelte", async () => ({
  default: (await import("#lib/components/reports/LineChartStub.test.svelte")).default,
}));
vi.mock("#lib/components/ambulatory-glucose-profile/index.js", async () => ({
  AmbulatoryGlucoseProfile: (await import("#lib/components/reports/LineChartStub.test.svelte")).default,
}));
vi.mock("#lib/components/reports/HourlyBolusChart.svelte", async () => ({
  default: (await import("#lib/components/reports/LineChartStub.test.svelte")).default,
}));

import IdpPage from "./+page.svelte";

const NOTE = /place each reading at the local time its device reported/;

function respond(timeZoneUnavailableReason?: TimeZoneUnavailableReason) {
  resource.current = {
    readingCount: 0,
    boluses: [],
    activeSchedules: [],
    analysis: { reliability: { meetsReliabilityCriteria: false, daysOfData: 3, recommendedMinimumDays: 14 }, basicStats: {}, glycemicVariability: {}, timeInRange: { percentages: {} } },
    averagedStats: [],
    timeZoneUnavailableReason,
    dateRange: { from: "2026-03-01", to: "2026-03-14", lastUpdated: "2026-03-14T00:00:00Z" },
  };
}

describe("insulin dosing profile report", () => {
  beforeEach(() => {
    resource.current = undefined;
  });

  it.each([
    [TimeZoneUnavailableReason.Share, /A shared link does not show the time zone/],
    [TimeZoneUnavailableReason.NotConfigured, /do not have a time zone/],
    [TimeZoneUnavailableReason.Unrecognised, /does not recognize the time zone/],
    [TimeZoneUnavailableReason.LookupFailed, /did not load/],
  ])("says why range figures are limited when range analytics carries %s", async (reason, sentence) => {
    respond(reason);
    render(IdpPage);

    await expect.element(page.getByText(NOTE)).toBeVisible();
    await expect.element(page.getByText(sentence)).toBeVisible();
  });

  it("shows no time zone note when range analytics carries no reason", async () => {
    respond();
    render(IdpPage);

    await expect.element(page.getByText("0 readings")).toBeVisible();
    await expect.element(page.getByText(NOTE)).not.toBeInTheDocument();
  });

  it("states the cause once and folds the UTC days into the note when reliability falls short", async () => {
    respond(TimeZoneUnavailableReason.NotConfigured);
    render(IdpPage);

    await expect.element(page.getByText(/Days\s+of\s+data\s+are\s+counted\s+on\s+UTC\s+days\./)).toBeVisible();
    await expect.element(page.getByText(/Based on 3 days of data/)).toBeVisible();
    expect(page.getByText(/do not have a time zone/).elements()).toHaveLength(1);
  });
});

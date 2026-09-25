import { render } from "vitest-browser-svelte";
import type { ComponentProps } from "svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";
import { remoteQuery } from "$lib/test-stubs/remote-resource";
import {
  WidgetId,
  WidgetPlacement,
  type FeatureSettings,
  type WidgetConfig,
} from "$lib/api/generated/nocturne-api-client";
import { dashboardTopWidgets } from "$lib/stores/appearance-store.svelte";
import { transformChartData } from "$lib/utils/chart-data-transform";

// The dashboard's own sections are stood down to markers, one testid each: what
// is under test is which of them the page renders, not what any of them shows.
vi.mock("$lib/components/dashboard", async () => ({
  CurrentBGDisplay: (
    await import("$lib/test-stubs/CurrentBgDisplay.test-stub.svelte")
  ).default,
  GlucoseChartCard: (
    await import("$lib/test-stubs/GlucoseChartCard.test-stub.svelte")
  ).default,
  RecentEntriesCard: (
    await import("$lib/test-stubs/RecentEntriesCard.test-stub.svelte")
  ).default,
  RecentTreatmentsCard: (
    await import("$lib/test-stubs/RecentTreatmentsCard.test-stub.svelte")
  ).default,
  WidgetGrid: (await import("$lib/test-stubs/WidgetGrid.test-stub.svelte"))
    .default,
}));

vi.mock("$api/generated/connectorStatus.generated.remote", () => ({
  getStatus: () => remoteQuery(() => []),
}));

import DashboardPageHarness from "./DashboardPageHarness.test.svelte";

type HarnessProps = ComponentProps<typeof DashboardPageHarness>;

const PINNED = [WidgetId.BgDelta, WidgetId.TirChart, WidgetId.Tdd];

function features(...widgets: WidgetConfig[]): FeatureSettings {
  return { widgets };
}

function mainSection(id: WidgetId, enabled: boolean): WidgetConfig {
  return { id, enabled, placement: WidgetPlacement.Main };
}

// A reading on hand takes the page down its "this instance has data" path, so
// the chart area renders its chart rather than the first-reading check.
const pageData: HarnessProps["data"] = {
  tenantless: false,
  isAuthenticated: true,
  isShareHost: false,
  canViewRealtimeData: false,
  isDemo: false,
  isGuestSession: false,
  isPlatformAdmin: false,
  isPlatformAccessGrant: false,
  tenantSlug: "sleepy",
  baseDomain: null,
  guestExpiresAt: null,
  nextResetAt: null,
  lastSignIn: null,
  dashboardSlugs: [],
  effectivePermissions: [],
  limitTo24Hours: false,
  displayPreferences: [],
  displayLanguage: "en",
  serverPreferences: null,
  user: {
    subjectId: "subject-1",
    name: "Test User",
    roles: [],
    permissions: [],
  },
  initialChartData: transformChartData({
    glucoseData: [{ time: Date.now(), sgv: 120 }],
  }),
  streamed: { historicalChartData: Promise.resolve(null) },
};

const grid = () => page.getByTestId("widget-grid");
const glucoseChart = () => page.getByTestId("glucose-chart");
const recentEntries = () => page.getByTestId("recent-entries");
const recentTreatments = () => page.getByTestId("recent-treatments");

function renderDashboard(settings: FeatureSettings) {
  render(DashboardPageHarness, {
    props: { features: settings, data: pageData },
  });
}

describe("dashboard page", () => {
  beforeEach(() => {
    dashboardTopWidgets.hydrate(PINNED);
  });

  it("keeps the user's top widgets when the tenant turns the Statistics section off", async () => {
    renderDashboard(features(mainSection(WidgetId.Statistics, false)));

    await expect.element(grid()).toBeVisible();
    await expect.element(grid()).toHaveTextContent(PINNED.join(","));
  });

  it("renders the top grid from the per-user list, not from tenant settings", async () => {
    dashboardTopWidgets.hydrate([WidgetId.Clock]);

    renderDashboard(
      features(
        mainSection(WidgetId.Statistics, false),
        mainSection(WidgetId.DailyStats, false),
        mainSection(WidgetId.Treatments, false)
      )
    );

    await expect.element(grid()).toHaveTextContent(WidgetId.Clock);
  });

  it("renders every main section the tenant leaves on", async () => {
    renderDashboard(features());

    await expect.element(glucoseChart()).toBeVisible();
    await expect.element(recentEntries()).toBeVisible();
    await expect.element(recentTreatments()).toBeVisible();
  });

  // The counterpart to the two above: the gates that own a section still hide
  // it, so moving the grid out of one of them did not loosen the rest.
  it("still hides each main section its own row disables", async () => {
    renderDashboard(
      features(
        mainSection(WidgetId.GlucoseChart, false),
        mainSection(WidgetId.DailyStats, false),
        mainSection(WidgetId.Treatments, false)
      )
    );

    await expect.element(grid()).toBeVisible();
    await expect.element(glucoseChart()).not.toBeInTheDocument();
    await expect.element(recentEntries()).not.toBeInTheDocument();
    await expect.element(recentTreatments()).not.toBeInTheDocument();
  });
});

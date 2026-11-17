import { render } from "vitest-browser-svelte";
import type { ComponentProps } from "svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";
import { remoteQuery } from "#lib/test-stubs/remote-resource.js";
import {
  WidgetId,
  WidgetPlacement,
  type FeatureSettings,
  type WidgetConfig,
} from "#lib/api/generated/nocturne-api-client.js";
import { dashboardTopWidgets } from "#lib/stores/appearance-store.svelte.js";
import { transformChartData } from "#lib/utils/chart-data-transform.js";

// The dashboard's own sections are stood down to markers, one testid each: what
// is under test is which of them the page renders, not what any of them shows.
vi.mock("#lib/components/dashboard/index.js", async () => ({
  CurrentBGDisplay: (
    await import("#lib/test-stubs/CurrentBgDisplay.test-stub.svelte")
  ).default,
  GlucoseChartCard: (
    await import("#lib/test-stubs/GlucoseChartCard.test-stub.svelte")
  ).default,
  RecentEntriesCard: (
    await import("#lib/test-stubs/RecentEntriesCard.test-stub.svelte")
  ).default,
  RecentTreatmentsCard: (
    await import("#lib/test-stubs/RecentTreatmentsCard.test-stub.svelte")
  ).default,
  WidgetGrid: (await import("#lib/test-stubs/WidgetGrid.test-stub.svelte"))
    .default,
}));

const uiSettings = vi.hoisted(() => ({
  features: {} as FeatureSettings,
  predictionEnabled: undefined as boolean | undefined,
}));

vi.mock("#lib/api/ui-settings.remote.js", () => ({
  getUiSettings: () =>
    remoteQuery(() => ({
      features: uiSettings.features,
      algorithm: { prediction: { enabled: uiSettings.predictionEnabled } },
    })),
}));

vi.mock("#lib/api/generated/connectorStatus.generated.remote.js", () => ({
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
  hasAccount: true,
  isShareHost: false,
  canViewRealtimeData: false,
  showBackupSignInPrompt: false,
  tenantTargets: [],
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
  refusedAsDemoSubject: false,
  onboardingComplete: true,
  pillRows: { desktop: 1, mobile: 1 },
  displayPreferences: [],
  displayLanguage: "en",
  serverPreferences: null,
  tenantPreferences: null,
  user: {
    subjectId: "subject-1",
    name: "Test User",
    roles: [],
    permissions: [],
  },
  initialChartData: transformChartData({
    glucoseData: [{ time: Date.now(), sgv: 120 }],
  }),
  plotSnapshot: null,
  initialUiSettings: null,
  initialPredictionsAvailable: null,
  initialWindowStart: Date.now() - 6 * 60 * 60 * 1000,
  streamed: { historicalChartData: Promise.resolve(null) },
};

const grid = () => page.getByTestId("widget-grid");
const glucoseChart = () => page.getByTestId("glucose-chart");
const recentEntries = () => page.getByTestId("recent-entries");
const recentTreatments = () => page.getByTestId("recent-treatments");

function renderDashboard(
  settings: FeatureSettings,
  {
    data = pageData,
    withRealtimeStore = false,
    currentReadingLoaded = false,
  }: {
    data?: HarnessProps["data"];
    withRealtimeStore?: boolean;
    currentReadingLoaded?: boolean;
  } = {}
) {
  uiSettings.features = settings;
  render(DashboardPageHarness, {
    props: { data, withRealtimeStore, currentReadingLoaded },
  });
}

describe("dashboard page", () => {
  beforeEach(() => {
    dashboardTopWidgets.hydrate(PINNED);
    uiSettings.predictionEnabled = undefined;
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

  it("holds the chart's plot while a realtime viewer's starting data loads", async () => {
    renderDashboard(features(), {
      data: { ...pageData, canViewRealtimeData: true },
      withRealtimeStore: true,
    });

    await expect
      .element(glucoseChart())
      .toHaveAttribute("data-defer-drawing", "true");
  });

  it("draws the chart's plot once the current reading has landed, ahead of the rest of the starting data", async () => {
    renderDashboard(features(), {
      data: { ...pageData, canViewRealtimeData: true },
      withRealtimeStore: true,
      currentReadingLoaded: true,
    });

    await expect
      .element(glucoseChart())
      .toHaveAttribute("data-defer-drawing", "false");
  });

  it("draws the chart's plot straight away for a viewer without realtime data", async () => {
    renderDashboard(features(), { withRealtimeStore: true });

    await expect
      .element(glucoseChart())
      .toHaveAttribute("data-defer-drawing", "false");
  });

  it("shows predictions on the chart by default", async () => {
    renderDashboard(features());

    await expect
      .element(glucoseChart())
      .toHaveAttribute("data-show-predictions", "true");
  });

  it("hides predictions on the chart when the algorithm settings turn them off", async () => {
    uiSettings.predictionEnabled = false;
    renderDashboard(features());

    await expect
      .element(glucoseChart())
      .toHaveAttribute("data-show-predictions", "false");
  });
});

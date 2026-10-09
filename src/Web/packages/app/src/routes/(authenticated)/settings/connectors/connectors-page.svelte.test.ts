import { beforeEach, describe, expect, it, vi } from "vitest";
import { render } from "vitest-browser-svelte";
import { page as browserPage } from "vitest/browser";
import { page } from "$app/state";
import ConnectorsPage from "./+page.svelte";

const mocks = vi.hoisted(() => ({
  googleHealth: vi.fn(),
  refreshGoogleHealth: vi.fn().mockResolvedValue(undefined),
  refreshOverview: vi.fn().mockResolvedValue(undefined),
  refreshStatuses: vi.fn().mockResolvedValue(undefined),
  overview: vi.fn(),
}));

vi.mock("$api/generated/googleHealths.generated.remote", () => ({
  getGoogleHealth: mocks.googleHealth,
}));
vi.mock("$api/generated/connectorStatus.generated.remote", () => ({
  getStatus: () => ({ current: [], refresh: mocks.refreshStatuses }),
}));
vi.mock("$api/generated/services.generated.remote", async (importOriginal) => ({
  ...(await importOriginal<
    typeof import("$api/generated/services.generated.remote")
  >()),
  getServicesOverview: mocks.overview,
  getConnectorCapabilities: () => ({ current: null }),
  triggerConnectorSync: vi.fn(),
}));
vi.mock("$lib/stores/realtime-store.svelte", () => ({
  getRealtimeStore: () => ({ syncProgressByConnector: {} }),
}));

describe("connector overview Google Health authorization", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mocks.overview.mockReturnValue({
      current: null,
      refresh: mocks.refreshOverview,
    });
    mocks.googleHealth.mockReturnValue({
      current: null,
      refresh: mocks.refreshGoogleHealth,
    });
  });

  it.each([
    { loading: true, error: undefined, cached: false, label: "Loading..." },
    {
      loading: false,
      error: new Error("status failed"),
      cached: false,
      label: "Status unavailable",
    },
    { loading: true, error: undefined, cached: true, label: "Loading..." },
    {
      loading: false,
      error: new Error("status failed"),
      cached: true,
      label: "Status unavailable",
    },
  ])(
    "preserves the generic source during $label (cached=$cached)",
    async ({ loading, error, cached, label }) => {
      page.data.effectivePermissions = ["tenant.settings"];
      page.data.refusedAsDemoSubject = false;
      mocks.overview.mockReturnValue({
        current: {
          availableConnectors: [{ id: "googlehealth", name: "Google Health" }],
          activeDataSources: [
            {
              id: "google",
              name: "Existing Google records",
              sourceType: "google-health-connector",
              deviceId: "google-health-connector",
            },
          ],
        },
        refresh: mocks.refreshOverview,
      });
      mocks.googleHealth.mockReturnValue({
        current: cached ? { configured: true, connected: true } : undefined,
        loading,
        error,
        refresh: mocks.refreshGoogleHealth,
      });
      render(ConnectorsPage);
      await expect
        .element(
          browserPage.getByText("Existing Google records", { exact: true })
        )
        .toBeVisible();
      await expect
        .element(
          browserPage.getByText("No data sources detected", { exact: true })
        )
        .not.toBeInTheDocument();
      await expect
        .element(
          browserPage
            .getByRole("link", { name: /Google Health/ })
            .getByText(label, { exact: true })
        )
        .toBeVisible();
      await expect
        .element(browserPage.getByText("Not Configured", { exact: true }))
        .not.toBeInTheDocument();
    }
  );

  it("replaces the generic source only after configured status loads", async () => {
    page.data.effectivePermissions = ["tenant.settings"];
    page.data.refusedAsDemoSubject = false;
    mocks.overview.mockReturnValue({
      current: {
        availableConnectors: [{ id: "googlehealth", name: "Google Health" }],
        activeDataSources: [
          {
            id: "google",
            name: "Existing Google records",
            sourceType: "google-health-connector",
            deviceId: "google-health-connector",
          },
        ],
      },
      refresh: mocks.refreshOverview,
    });
    mocks.googleHealth.mockReturnValue({
      current: { configured: true, connected: true, selectedTypes: ["steps"] },
      loading: false,
      error: undefined,
      refresh: mocks.refreshGoogleHealth,
    });
    render(ConnectorsPage);
    await expect
      .element(browserPage.getByText("Google Health", { exact: true }).first())
      .toBeVisible();
    await expect
      .element(
        browserPage.getByText("Existing Google records", { exact: true })
      )
      .not.toBeInTheDocument();
    await expect
      .element(
        browserPage.getByText("No data sources detected", { exact: true })
      )
      .not.toBeInTheDocument();
    await expect
      .element(browserPage.getByText("Not Configured", { exact: true }))
      .not.toBeInTheDocument();
  });

  it.each([
    { permissions: ["glucose.read"], demo: false, allowed: false },
    { permissions: ["tenant.settings"], demo: true, allowed: false },
    { permissions: ["*"], demo: true, allowed: false },
    { permissions: ["tenant.settings"], demo: false, allowed: true },
    { permissions: ["*"], demo: false, allowed: true },
  ])(
    "gates status query and refresh for $permissions (demo=$demo)",
    async ({ permissions, demo, allowed }) => {
      page.data.effectivePermissions = permissions;
      page.data.refusedAsDemoSubject = demo;
      render(ConnectorsPage);

      await expect
        .element(
          browserPage
            .getByText("Failed to load services", { exact: true })
            .first()
        )
        .toBeVisible();
      expect(mocks.googleHealth).not.toHaveBeenCalled();
      await browserPage
        .getByRole("button", { name: "Refresh", exact: true })
        .click();
      expect(mocks.googleHealth).toHaveBeenCalledTimes(allowed ? 1 : 0);
      expect(mocks.refreshGoogleHealth).toHaveBeenCalledTimes(allowed ? 1 : 0);
    }
  );
});

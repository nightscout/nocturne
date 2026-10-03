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
  getServicesOverview: () => ({
    current: null,
    refresh: mocks.refreshOverview,
  }),
  getConnectorCapabilities: vi.fn(),
  triggerConnectorSync: vi.fn(),
}));
vi.mock("$lib/stores/realtime-store.svelte", () => ({
  getRealtimeStore: () => ({ syncProgressByConnector: {} }),
}));

describe("connector overview Google Health authorization", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mocks.googleHealth.mockReturnValue({
      current: null,
      refresh: mocks.refreshGoogleHealth,
    });
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

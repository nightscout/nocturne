import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, it, expect, vi } from "vitest";

let tenantsImpl: () => Promise<unknown>;
let connectorsResult: unknown = null;

vi.mock("$api/generated/tenants.generated.remote", () => ({
  getAll: () => ({ run: () => tenantsImpl() }),
}));

vi.mock("$api/generated/connectorAdmins.generated.remote", () => ({
  getTenantConnectors: () => ({ run: () => Promise.resolve(connectorsResult) }),
  resetTenantCursors: () => Promise.resolve({ jobId: "reset-job" }),
  getResetJobStatus: () => ({
    run: () =>
      Promise.resolve({
        state: "Failed",
        errorMessage: "connector_reset_failed",
        connectors: [],
        completedConnectors: 1,
        totalConnectors: 1,
      }),
  }),
  cancelResetJob: () => Promise.resolve(),
}));

import ConnectorCursorsPage from "./+page.svelte";

function rejection(status: number, message: string) {
  return Promise.reject({ status, body: { message } });
}

describe("settings/admin/connector-cursors", () => {
  beforeEach(() => {
    connectorsResult = null;
  });

  it("renders localized copy for the connector reset failure code", async () => {
    tenantsImpl = () =>
      Promise.resolve([
        { id: "tenant", displayName: "Example", slug: "example" },
      ]);
    connectorsResult = {
      connectors: [{ connectorName: "GoogleHealth", isHealthy: true }],
    };
    render(ConnectorCursorsPage, {});

    await page.getByText("Select a tenant", { exact: true }).click();
    await page.getByRole("option", { name: "Example (example)" }).click();
    await page
      .getByRole("button", { name: "Reset cursors", exact: true })
      .click();
    await page
      .getByRole("alertdialog")
      .getByRole("button", { name: "Reset cursors", exact: true })
      .click();

    await expect
      .element(
        page.getByText(
          "One or more connectors failed; review the connector details and retry the failed range."
        )
      )
      .toBeVisible();
    await expect
      .element(page.getByText("connector_reset_failed", { exact: true }))
      .not.toBeInTheDocument();
  });

  it("shows the server's reason when the tenant list cannot load", async () => {
    tenantsImpl = () =>
      rejection(503, "Tenant directory is locked while a restore runs.");

    render(ConnectorCursorsPage, {});

    await expect
      .element(
        page.getByText("Tenant directory is locked while a restore runs.")
      )
      .toBeVisible();
  });

  it("keeps its own sentence when the client wrote the reason", async () => {
    tenantsImpl = () => rejection(500, "Failed to execute remote function");

    render(ConnectorCursorsPage, {});

    await expect
      .element(page.getByText("Failed to load tenants."))
      .toBeVisible();
  });
});

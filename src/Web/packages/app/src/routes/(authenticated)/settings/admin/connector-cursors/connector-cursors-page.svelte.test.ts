import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi } from "vitest";

let tenantsImpl: () => Promise<unknown>;

vi.mock("$api/generated/tenants.generated.remote", () => ({
  getAll: () => ({ run: () => tenantsImpl() }),
}));

vi.mock("$api/generated/connectorAdmins.generated.remote", () => ({
  getTenantConnectors: () => ({ run: () => Promise.resolve(null) }),
  resetTenantCursors: () => Promise.resolve({}),
  getResetJobStatus: () => ({ run: () => Promise.resolve(null) }),
  cancelResetJob: () => Promise.resolve(),
}));

import ConnectorCursorsPage from "./+page.svelte";

function rejection(status: number, message: string) {
  return Promise.reject({ status, body: { message } });
}

describe("settings/admin/connector-cursors", () => {
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

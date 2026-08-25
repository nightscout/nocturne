import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";

let deleteConfigImpl: () => Promise<unknown>;
let deleteDataImpl: () => Promise<unknown>;

vi.mock("$lib/api/generated/configurations.generated.remote", () => ({
  deleteConfiguration: () => deleteConfigImpl(),
}));

vi.mock("$lib/api/generated/services.generated.remote", () => ({
  deleteConnectorData: () => deleteDataImpl(),
}));

import ConnectorDangerZone from "./ConnectorDangerZone.svelte";

// A rejected generated remote function is SvelteKit's `HttpError`: a plain
// `{ status, body }` with no `Error` in its prototype chain.
function rejection(status: number, message: string) {
  return Promise.reject({ status, body: { message } });
}

async function attemptDeleteConfiguration() {
  render(ConnectorDangerZone, {
    props: {
      connectorId: "dexcom",
      displayName: "Dexcom",
      hasExistingConfig: true,
      hasData: false,
      dataSummary: null,
    },
  });

  await page.getByRole("button", { name: "Delete Config", exact: true }).click();
  await page.getByRole("textbox").fill("DELETE CONFIGURATION");
  await page
    .getByRole("button", { name: "Delete Configuration", exact: true })
    .click();
}

async function attemptDeleteData() {
  render(ConnectorDangerZone, {
    props: {
      connectorId: "dexcom",
      displayName: "Dexcom",
      hasExistingConfig: false,
      hasData: true,
      dataSummary: null,
    },
  });

  await page.getByRole("button", { name: "Delete Data", exact: true }).click();
  await page.getByRole("textbox").fill("DELETE DATA");
  await page.getByRole("button", { name: "Delete All Data" }).click();
}

describe("ConnectorDangerZone", () => {
  beforeEach(() => {
    deleteConfigImpl = () => Promise.resolve({});
    deleteDataImpl = () => Promise.resolve({ success: true, totalDeleted: 1 });
  });

  it("shows the server's reason when deleting the configuration is refused", async () => {
    deleteConfigImpl = () =>
      rejection(409, "Stop the running sync before deleting this connector.");

    await attemptDeleteConfiguration();

    await expect
      .element(
        page.getByText("Stop the running sync before deleting this connector.")
      )
      .toBeInTheDocument();
  });

  it("keeps a server fault behind the component's own wording", async () => {
    deleteConfigImpl = () => rejection(500, "npgsql: connection reset");

    await attemptDeleteConfiguration();

    await expect
      .element(page.getByText("Failed to delete configuration").first())
      .toBeInTheDocument();
    await expect
      .element(page.getByText("npgsql: connection reset"))
      .not.toBeInTheDocument();
  });

  it("shows the server's reason when deleting synced data is refused", async () => {
    deleteDataImpl = () =>
      rejection(409, "A deduplication job is still running on this data.");

    await attemptDeleteData();

    await expect
      .element(
        page.getByText("A deduplication job is still running on this data.")
      )
      .toBeInTheDocument();
  });
});

import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";
import type { DataSourceInfo } from "$api-clients";

let deleteImpl: () => Promise<unknown>;

vi.mock("$api/generated/services.generated.remote", () => ({
  deleteDataSourceData: () => deleteImpl(),
}));

import DataSourceManageDialog from "./DataSourceManageDialog.svelte";

/**
 * What a rejected remote function hands the dialog: SvelteKit's `HttpError` is
 * a plain `{ status, body }` object and not an `Error`, so the status is the
 * only part of it a caller can read.
 */
function httpError(status: number, message: string) {
  return { status, body: { message } };
}

const dataSource: DataSourceInfo = {
  id: "11111111-1111-1111-1111-111111111111",
  name: "Dexcom G7",
  category: "cgm",
  status: "active",
  totalEntries: 4200,
};

async function attemptDelete() {
  render(DataSourceManageDialog, {
    props: { open: true, selectedDataSource: dataSource },
  });

  await page.getByRole("button", { name: "Delete Data..." }).click();
  await page.getByRole("textbox").fill("DELETE");
  await page.getByRole("button", { name: "Delete All Data" }).click();
}

describe("DataSourceManageDialog", () => {
  beforeEach(() => {
    deleteImpl = () => Promise.resolve({ success: true, totalDeleted: 1 });
  });

  it("reports data that was already gone as nothing left to delete", async () => {
    deleteImpl = () => Promise.reject(httpError(404, "Not found"));

    await attemptDelete();

    await expect
      .element(page.getByText("Nothing left to delete"))
      .toBeInTheDocument();
    await expect
      .element(page.getByText("This data source has no data left to delete."))
      .toBeInTheDocument();
  });

  it("still reports a delete that failed as a failure", async () => {
    deleteImpl = () => Promise.reject(httpError(500, "Failed to delete data"));

    await attemptDelete();

    const failureWording = page.getByText("Failed to delete data");
    await expect.element(failureWording.first()).toBeInTheDocument();
    // the headline and the message below it each carry the wording
    expect(failureWording.elements()).toHaveLength(2);
    await expect
      .element(page.getByText("Nothing left to delete"))
      .not.toBeInTheDocument();
  });
});

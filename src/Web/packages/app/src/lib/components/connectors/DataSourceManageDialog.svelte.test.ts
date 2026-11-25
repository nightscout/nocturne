import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";
import { error } from "@sveltejs/kit";
import { page as pageState } from "$app/state";
import type { ConnectionStream } from "@nocturne/api-client";

let deleteImpl: () => Promise<unknown>;
let deleteArgs: unknown[];

vi.mock("#lib/api/generated/services.generated.remote.js", () => ({
  deleteDataSourceData: (args: unknown) => {
    deleteArgs.push(args);
    return deleteImpl();
  },
}));

const toastSuccess = vi.hoisted(() => vi.fn());
vi.mock("svelte-sonner", () => ({ toast: { success: toastSuccess } }));

import DataSourceManageDialog from "./DataSourceManageDialog.svelte";

const dataSource: ConnectionStream = {
  key: "11111111-1111-1111-1111-111111111111",
  name: "Dexcom G7",
  totalRecords: 4200,
  canDelete: true,
  recordCounts: { Glucose: 4100, Boluses: 100 },
};

async function attemptDelete() {
  render(DataSourceManageDialog, {
    props: { open: true, source: dataSource, windowDays: 30 },
  });

  await page.getByRole("button", { name: "Delete data..." }).click();
  await page.getByRole("textbox").fill("DELETE");
  await page.getByRole("button", { name: "Delete all data" }).click();
}

describe("DataSourceManageDialog", () => {
  beforeEach(() => {
    deleteImpl = () => Promise.resolve({ success: true, totalDeleted: 1 });
    deleteArgs = [];
    toastSuccess.mockClear();
    pageState.data = { effectivePermissions: ["tenant.settings"] };
  });

  it("deletes without allowing re-import by default", async () => {
    await attemptDelete();

    await expect.poll(() => deleteArgs).toEqual([
      { id: dataSource.key, allowReimport: false },
    ]);
  });

  it("reports how many records it deleted, by name", async () => {
    deleteImpl = () => Promise.resolve({ success: true, totalDeleted: 4200 });

    await attemptDelete();

    await expect
      .poll(() => toastSuccess.mock.calls)
      .toEqual([["Deleted 4,200 records from Dexcom G7"]]);
  });

  it("lists the record types of the source in place of a fixed list", async () => {
    render(DataSourceManageDialog, {
      props: { open: true, source: dataSource, windowDays: 30 },
    });

    await page.getByRole("button", { name: "Delete data..." }).click();
    const confirm = page.getByRole("alertdialog");
    await expect.element(confirm).toHaveTextContent("Glucose");
    await expect.element(confirm).toHaveTextContent("4,100");
    await expect.element(confirm).toHaveTextContent("Boluses");
    await expect.element(confirm).not.toHaveTextContent("device status");
  });

  it("keeps the delete disabled until the shown word is typed", async () => {
    render(DataSourceManageDialog, {
      props: { open: true, source: dataSource, windowDays: 30 },
    });

    await page.getByRole("button", { name: "Delete data..." }).click();
    const confirm = page.getByRole("button", { name: "Delete all data" });
    await page.getByRole("textbox").fill("delete");
    await expect.element(confirm).toBeDisabled();
    await page.getByRole("textbox").fill("DELETE");
    await expect.element(confirm).toBeEnabled();
  });

  it("tells its opener when the reader cancels", async () => {
    const onCancel = vi.fn();
    render(DataSourceManageDialog, {
      props: { open: true, source: dataSource, windowDays: 30, onCancel },
    });

    await page.getByRole("button", { name: "Cancel" }).click();
    expect(onCancel).toHaveBeenCalledOnce();
  });

  it("allows re-import when the option is checked", async () => {
    render(DataSourceManageDialog, {
      props: { open: true, source: dataSource, windowDays: 30 },
    });

    await page.getByRole("button", { name: "Delete data..." }).click();
    await page.getByRole("checkbox", { name: /Allow re-import/ }).click();
    await page.getByRole("textbox").fill("DELETE");
    await page.getByRole("button", { name: "Delete and allow re-import" }).click();

    await expect.poll(() => deleteArgs).toEqual([
      { id: dataSource.key, allowReimport: true },
    ]);
  });

  it("hides the delete control from a viewer", async () => {
    pageState.data = { effectivePermissions: ["glucose.read"] };

    render(DataSourceManageDialog, {
      props: { open: true, source: dataSource, windowDays: 30 },
    });

    await expect.element(page.getByText("Dexcom G7")).toBeVisible();
    expect(
      page.getByRole("button", { name: "Delete data..." }).elements()
    ).toHaveLength(0);
  });

  it("tells a caretaker why the delete is not theirs, and offers no delete", async () => {
    pageState.data = { effectivePermissions: ["glucose.read", "treatments.readwrite", "connectors.readwrite"] };

    render(DataSourceManageDialog, {
      props: { open: true, source: dataSource, windowDays: 30 },
    });

    await expect
      .element(page.getByTestId("data-source-delete-reason"))
      .toHaveTextContent("Only members who can manage this site's settings can delete synced data.");
    expect(page.getByRole("button", { name: "Delete data..." }).elements()).toHaveLength(0);
  });

  it("gives a viewer and a settings holder no reason", async () => {
    pageState.data = { effectivePermissions: ["glucose.read"] };
    const viewer = render(DataSourceManageDialog, {
      props: { open: true, source: dataSource, windowDays: 30 },
    });
    await expect.element(page.getByText("Dexcom G7")).toBeVisible();
    expect(page.getByTestId("data-source-delete-reason").elements()).toHaveLength(0);
    viewer.unmount();

    pageState.data = { effectivePermissions: ["tenant.settings"] };
    render(DataSourceManageDialog, {
      props: { open: true, source: dataSource, windowDays: 30 },
    });
    await expect.element(page.getByRole("button", { name: "Delete data..." })).toBeVisible();
    expect(page.getByTestId("data-source-delete-reason").elements()).toHaveLength(0);
  });

  it("offers the delete control to a member holding tenant.settings", async () => {
    render(DataSourceManageDialog, {
      props: { open: true, source: dataSource, windowDays: 30 },
    });

    await expect
      .element(page.getByRole("button", { name: "Delete data..." }))
      .toBeVisible();
  });

  it("offers the delete control to an owner holding the wildcard scope", async () => {
    pageState.data = { effectivePermissions: ["*"] };

    render(DataSourceManageDialog, {
      props: { open: true, source: dataSource, windowDays: 30 },
    });

    await expect
      .element(page.getByRole("button", { name: "Delete data..." }))
      .toBeVisible();
  });

  it("reports data that was already gone as nothing left to delete", async () => {
    deleteImpl = async () => error(404, "Not found");

    await attemptDelete();

    await expect
      .element(page.getByText("Nothing left to delete"))
      .toBeInTheDocument();
    await expect
      .element(page.getByText("This data source has no data left to delete."))
      .toBeInTheDocument();
  });

  it("keeps the dialog open with the reason when the server refuses the delete", async () => {
    deleteImpl = async () => error(409, "A sync of the connector is running.");

    await attemptDelete();

    await expect
      .element(
        page
          .getByRole("alertdialog")
          .getByText("A sync of the connector is running.")
      )
      .toBeVisible();
    expect(toastSuccess).not.toHaveBeenCalled();
  });

  it("offers no delete for a source the API does not let it delete", async () => {
    render(DataSourceManageDialog, {
      props: {
        open: true,
        source: { ...dataSource, canDelete: false },
        windowDays: 30,
      },
    });

    await expect.element(page.getByText("Dexcom G7")).toBeVisible();
    expect(
      page.getByRole("button", { name: "Delete data..." }).elements()
    ).toHaveLength(0);
  });

  it("keeps a server fault behind the dialog's own wording", async () => {
    deleteImpl = async () => error(500, "db connection reset");

    await attemptDelete();

    const failureWording = page.getByText("Failed to delete data");
    await expect.element(failureWording.first()).toBeInTheDocument();
    // the headline and the message below it each carry the wording
    expect(failureWording.elements()).toHaveLength(2);
    await expect
      .element(page.getByText("db connection reset"))
      .not.toBeInTheDocument();
    await expect
      .element(page.getByText("Nothing left to delete"))
      .not.toBeInTheDocument();
  });
});

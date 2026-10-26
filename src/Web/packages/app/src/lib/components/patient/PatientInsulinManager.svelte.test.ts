import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { remoteForm, remoteQuery } from "#lib/test-stubs/remote-resource.js";

const { deleteInsulin } = vi.hoisted(() => ({
  deleteInsulin: vi.fn().mockResolvedValue(undefined),
}));

let insulinsCurrent: unknown[] = [];
let catalogCurrent: unknown[] = [];

vi.mock("#lib/api/generated/patientRecords.generated.remote.js", () => ({
  getInsulins: () => remoteQuery(() => insulinsCurrent),
  createInsulin: remoteForm(),
  updateInsulin: remoteForm(),
  deleteInsulin: (...args: unknown[]) => deleteInsulin(...args),
}));

vi.mock("#lib/api/generated/insulinCatalogs.generated.remote.js", () => ({
  getCatalog: () => remoteQuery(() => catalogCurrent),
}));

import PatientInsulinManager from "./PatientInsulinManager.svelte";

describe("PatientInsulinManager", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    insulinsCurrent = [];
    catalogCurrent = [];
  });

  describe("inline variant", () => {
    it("names an insulin with no name as unknown", async () => {
      insulinsCurrent = [
        { id: "i1", name: "Fiasp", insulinCategory: "RapidActing", role: "Bolus", dia: 4, isPrimary: true },
        { id: "i2", name: "", insulinCategory: "LongActing", role: "Basal" },
      ];
      render(PatientInsulinManager, { variant: "inline" });

      await expect.element(page.getByText("Fiasp")).toBeVisible();
      await expect.element(page.getByText("Unknown insulin")).toBeVisible();
    });

    it("opens the add form with its fields and a disabled add button", async () => {
      render(PatientInsulinManager, { variant: "inline" });

      await page.getByRole("button", { name: "Add insulin" }).click();

      await expect.element(page.getByText("Add an insulin")).toBeVisible();
      await expect.element(page.getByText("Brand / name")).toBeVisible();
      await expect.element(page.getByText("Duration of insulin action")).toBeVisible();
      await expect.element(page.getByText("Start date")).not.toBeInTheDocument();
      await expect.element(page.getByRole("button", { name: "Add insulin" })).toBeDisabled();
    });

    it("closes the add form on cancel", async () => {
      render(PatientInsulinManager, { variant: "inline" });

      await page.getByRole("button", { name: "Add insulin" }).click();
      await page.getByRole("button", { name: "Cancel" }).click();

      await expect.element(page.getByText("Add an insulin")).not.toBeInTheDocument();
    });
  });

  describe("dialog variant", () => {
    it("opens an add dialog with the extended fields", async () => {
      render(PatientInsulinManager, { variant: "dialog" });

      await expect.element(page.getByText(/No insulins added yet/)).toBeVisible();
      await page.getByRole("button", { name: "Add insulin" }).click();

      await expect.element(page.getByRole("heading", { name: "Add insulin" })).toBeVisible();
      await expect.element(page.getByText("Add an insulin to your patient record.")).toBeVisible();
      await expect.element(page.getByLabelText("Start date")).toBeVisible();
      await expect.element(page.getByLabelText("End date")).toBeVisible();
    });

    it("opens an edit dialog filled from the insulin", async () => {
      insulinsCurrent = [
        {
          id: "i1",
          name: "Tresiba",
          insulinCategory: "UltraLongActing",
          role: "Basal",
          isCurrent: true,
          dia: 42,
          startDate: "2026-01-15T00:00:00Z",
        },
      ];
      render(PatientInsulinManager, { variant: "dialog" });

      await expect.element(page.getByText("Tresiba")).toBeVisible();
      // The row's first icon button edits, the second deletes.
      await page.getByRole("button").nth(0).click();

      await expect.element(page.getByRole("heading", { name: "Edit insulin" })).toBeVisible();
      await expect.element(page.getByLabelText("Brand / name")).toHaveValue("Tresiba");
      await expect.element(page.getByLabelText("Start date")).toHaveValue("2026-01-15");
    });
  });
});

import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { remoteForm, remoteQuery } from "#lib/test-stubs/remote-resource.js";

const { createInsulin } = vi.hoisted(() => ({ createInsulin: vi.fn() }));

let insulinsCurrent: unknown[] = [];
let catalogCurrent: unknown[] = [];

vi.mock("#lib/api/generated/patientRecords.generated.remote.js", () => ({
  getInsulins: () => remoteQuery(() => insulinsCurrent),
  createInsulin: remoteForm((data: unknown) => createInsulin(data)),
}));

vi.mock("#lib/api/generated/insulinCatalogs.generated.remote.js", () => ({
  getCatalog: () => remoteQuery(() => catalogCurrent),
}));

import TreatmentEditDialog from "./TreatmentEditDialog.svelte";
import type { EntryRecord } from "#lib/constants/entry-categories.js";
import { toUpdateEntryInput } from "#routes/(authenticated)/reports/treatments/entry-request.js";

function renderDialog(record: EntryRecord | null, onSave = vi.fn()) {
  render(TreatmentEditDialog, {
    props: { open: true, record, onClose: vi.fn(), onSave },
  });
  return onSave;
}

const mills = new Date("2026-03-01T08:30:00Z").getTime();

describe("TreatmentEditDialog", () => {
  beforeEach(() => {
    insulinsCurrent = [];
    catalogCurrent = [];
  });

  it("titles an existing record as an edit and shows its carbs fields", async () => {
    renderDialog({
      kind: "carbs",
      data: { id: "c1", mills, carbs: 45, absorptionTime: 180, dataSource: "manual" },
    });

    await expect.element(page.getByText("Edit record")).toBeVisible();
    await expect.element(page.getByText("Date & time")).toBeVisible();
    await expect.element(page.getByLabelText("Absorption time (min)")).toHaveValue(180);
    await expect.element(page.getByLabelText("Carb time (min)")).toBeVisible();
    await expect.element(page.getByRole("button", { name: "Save changes" })).toBeVisible();
  });

  it("titles a record without an id as an add", async () => {
    renderDialog({ kind: "carbs", data: { mills, carbs: 0 } });

    await expect.element(page.getByText("Add record")).toBeVisible();
    await expect.element(page.getByRole("button", { name: "Create record" })).toBeVisible();
  });

  it("saves the edited carbs fields", async () => {
    const onSave = renderDialog({ kind: "carbs", data: { id: "c1", mills, carbs: 45 } });

    await page.getByLabelText("Absorption time (min)").fill("120");
    await page.getByRole("button", { name: "Save changes" }).click();

    expect(onSave).toHaveBeenCalledTimes(1);
    const saved = onSave.mock.calls[0][0] as EntryRecord;
    expect(saved.kind).toBe("carbs");
    expect(saved.data).toMatchObject({ id: "c1", carbs: 45, absorptionTime: 120 });
  });

  const fiasp = { id: "ins-fiasp", name: "Fiasp (Faster Aspart)", isCurrent: true, role: "Bolus", dia: 4, peak: 55, curve: "RapidActing", concentration: 100 };
  const humalog = { id: "ins-humalog", name: "Humalog", isCurrent: true, role: "Bolus", dia: 5, peak: 75, curve: "RapidActing", concentration: 100 };

  it("links an unlinked bolus to the insulin chosen in the picker", async () => {
    insulinsCurrent = [fiasp, humalog];
    const onSave = renderDialog({ kind: "bolus", data: { id: "b1", mills, insulin: 2, insulinType: "FIASP" } });

    await page.getByText("Select insulin...").click();
    await page.getByRole("option", { name: "Fiasp (Faster Aspart)" }).click();
    await page.getByRole("button", { name: "Save changes" }).click();

    const saved = onSave.mock.calls[0][0] as EntryRecord;
    if (saved.kind !== "bolus") throw new Error("expected a bolus");
    expect(saved.data.insulinContext).toMatchObject({ patientInsulinId: "ins-fiasp", insulinName: "Fiasp (Faster Aspart)", dia: 4 });
    expect(toUpdateEntryInput(saved).data).toMatchObject({ patientInsulinId: "ins-fiasp", insulinType: "Fiasp (Faster Aspart)" });
  });

  it("relinks a linked bolus to another insulin", async () => {
    insulinsCurrent = [fiasp, humalog];
    const onSave = renderDialog({
      kind: "bolus",
      data: { id: "b1", mills, insulin: 2, insulinType: "Fiasp (Faster Aspart)", insulinContext: { patientInsulinId: "ins-fiasp", insulinName: "Fiasp (Faster Aspart)" } },
    });

    await page.getByText("Fiasp (Faster Aspart)").click();
    await page.getByRole("option", { name: "Humalog" }).click();
    await page.getByRole("button", { name: "Save changes" }).click();

    const saved = onSave.mock.calls[0][0] as EntryRecord;
    expect(toUpdateEntryInput(saved).data).toMatchObject({ patientInsulinId: "ins-humalog" });
  });

  it("says no record is selected when given none", async () => {
    renderDialog(null);

    await expect.element(page.getByText("No record selected")).toBeVisible();
    await expect.element(page.getByText("Select a row in the table to view and edit its details.")).toBeVisible();
  });

  it("opens the add-insulin form from a bolus's insulin picker", async () => {
    renderDialog({ kind: "bolus", data: { id: "b1", mills, insulin: 2 } });

    await page.getByText("Select insulin...").click();
    await page.getByRole("option", { name: "Add new insulin..." }).click();

    await expect.element(page.getByText("Add new insulin")).toBeVisible();
    await expect.element(page.getByText("Brand / name")).toBeVisible();
    await expect.element(page.getByText("Duration of insulin action")).toBeVisible();
    await expect.element(page.getByRole("button", { name: "Add insulin" })).toBeDisabled();
  });

  it("shows why the server refused the new insulin, and keeps the form", async () => {
    createInsulin.mockRejectedValueOnce({ status: 400, body: { message: "That insulin already exists" } });
    renderDialog({ kind: "bolus", data: { id: "b1", mills, insulin: 2 } });

    await page.getByText("Select insulin...").click();
    await page.getByRole("option", { name: "Add new insulin..." }).click();
    await page.getByText("Select category").click();
    await page.getByRole("option").first().click();
    await page.getByLabelText("Brand / name").fill("Fiasp");
    await page.getByRole("button", { name: "Add insulin" }).click();

    await expect.element(page.getByRole("alert")).toHaveTextContent("That insulin already exists");
    await expect.element(page.getByLabelText("Brand / name")).toHaveValue("Fiasp");
    await expect.element(page.getByRole("button", { name: "Add insulin" })).toBeEnabled();
  });
});

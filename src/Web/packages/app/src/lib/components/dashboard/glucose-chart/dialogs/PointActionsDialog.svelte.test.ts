import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi } from "vitest";
import type { EntryRecord } from "#lib/constants/entry-categories.js";
import PointActionsDialog from "./PointActionsDialog.svelte";

const timestamp = new Date();

const bolus: EntryRecord = {
  kind: "bolus",
  data: { id: "b1", mills: timestamp.getTime(), insulin: 0.2 },
};

function props(overrides: { canLog?: boolean; inspectOptions?: [] } = {}) {
  return {
    open: true,
    timestamp,
    inspectOptions: [{ type: "glucose" as const, label: "Glucose", preview: "120 mg/dL" }],
    entries: [bolus],
    canLog: true,
    onInspect: vi.fn(),
    onEdit: vi.fn(),
    onLog: vi.fn(),
    onClose: vi.fn(),
    ...overrides,
  };
}

describe("PointActionsDialog", () => {
  it("lists the data to inspect and the entries to edit in one dialog", async () => {
    const p = props();
    render(PointActionsDialog, p);

    const dialog = page.getByRole("dialog");
    await dialog.getByRole("button", { name: /Glucose/ }).click();
    expect(p.onInspect).toHaveBeenCalledWith("glucose");

    await dialog.getByRole("button", { name: /0\.2/ }).click();
    expect(p.onEdit).toHaveBeenCalledWith(bolus);
  });

  it("offers Log event to a viewer who can record entries", async () => {
    const p = props();
    render(PointActionsDialog, p);

    await page.getByRole("button", { name: "Log event" }).click();
    expect(p.onLog).toHaveBeenCalled();
  });

  it("hides Log event from a viewer who cannot record entries", async () => {
    render(PointActionsDialog, props({ canLog: false }));

    await expect.element(page.getByRole("dialog")).toBeVisible();
    await expect.element(page.getByRole("button", { name: "Log event" })).not.toBeInTheDocument();
  });

  it("names only what it offers at a point with nothing to inspect", async () => {
    render(PointActionsDialog, props({ inspectOptions: [] }));

    await expect.element(page.getByText("Select an entry to edit.")).toBeVisible();
    await expect.element(page.getByRole("heading", { name: "Inspect" })).not.toBeInTheDocument();
  });
});

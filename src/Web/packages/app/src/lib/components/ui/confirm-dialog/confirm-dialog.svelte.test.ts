import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi } from "vitest";
import ConfirmDialog from "./confirm-dialog.svelte";

describe("ConfirmDialog", () => {
  it("runs the confirm action", async () => {
    const onConfirm = vi.fn();
    render(ConfirmDialog, {
      open: true,
      title: "Delete this entry?",
      confirmLabel: "Delete",
      onConfirm,
    });

    await page.getByRole("button", { name: "Delete" }).click();

    expect(onConfirm).toHaveBeenCalledOnce();
  });

  it("tells the owner the dialog closed when cancelled", async () => {
    const onOpenChange = vi.fn();
    render(ConfirmDialog, {
      open: true,
      title: "Delete this entry?",
      confirmLabel: "Delete",
      onOpenChange,
    });

    await page.getByRole("button", { name: "Cancel" }).click();

    // The sites that pass `open` one-way — settings/weight, settings/timezone,
    // PatientDeviceManager, PatientInsulinManager — have no other close path.
    expect(onOpenChange).toHaveBeenCalledWith(false);
  });

  it("leaves the dialog open when the action is confirmed", async () => {
    const onOpenChange = vi.fn();
    render(ConfirmDialog, {
      open: true,
      title: "Delete this entry?",
      confirmLabel: "Delete",
      onOpenChange,
      onConfirm: () => {},
    });

    await page.getByRole("button", { name: "Delete" }).click();

    // Callers close it themselves once their request settles, so a slow delete
    // keeps its dialog and its spinner on screen.
    expect(onOpenChange).not.toHaveBeenCalled();
    await expect
      .element(page.getByRole("button", { name: "Cancel" }))
      .toBeInTheDocument();
  });

  it("blocks the confirm button while busy, and leaves cancel alive", async () => {
    render(ConfirmDialog, {
      open: true,
      title: "Delete this entry?",
      confirmLabel: "Delete",
      busy: true,
    });

    await expect
      .element(page.getByRole("button", { name: "Delete" }))
      .toBeDisabled();
    await expect
      .element(page.getByRole("button", { name: "Cancel" }))
      .toBeEnabled();
  });
});

import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi } from "vitest";
import ConfirmDialog from "./confirm-dialog.svelte";

const base = { open: true, title: "Delete this entry?", confirmLabel: "Delete" };

describe("ConfirmDialog", () => {
  it("runs the confirm action", async () => {
    const onConfirm = vi.fn();
    render(ConfirmDialog, { ...base, onConfirm });

    await page.getByRole("button", { name: "Delete" }).click();

    expect(onConfirm).toHaveBeenCalledOnce();
  });

  it("tells the owner the dialog closed when cancelled", async () => {
    const onOpenChange = vi.fn();
    render(ConfirmDialog, { ...base, onOpenChange });

    await page.getByRole("button", { name: "Cancel" }).click();

    // The sites that pass `open` one-way — settings/weight, settings/timezone,
    // PatientDeviceManager, PatientInsulinManager — have no other close path.
    expect(onOpenChange).toHaveBeenCalledWith(false);
  });

  it("leaves the dialog open when the action is confirmed", async () => {
    const onOpenChange = vi.fn();
    render(ConfirmDialog, { ...base, onOpenChange, onConfirm: () => {} });

    await page.getByRole("button", { name: "Delete" }).click();

    // Callers close it themselves once their request settles, so a slow delete
    // keeps its dialog and its spinner on screen.
    expect(onOpenChange).not.toHaveBeenCalled();
  });

  it("blocks the confirm button while the action is in flight", async () => {
    render(ConfirmDialog, { ...base, busy: true });

    await expect
      .element(page.getByRole("button", { name: "Delete" }))
      .toBeDisabled();
  });

  it("still cancels while the action is in flight", async () => {
    const onOpenChange = vi.fn();
    render(ConfirmDialog, { ...base, busy: true, onOpenChange });

    await page.getByRole("button", { name: "Cancel" }).click();

    // bits-ui strips `disabled` off Cancel and only uses it to suppress its own
    // close handler, so a disabled Cancel is a live-looking button that does
    // nothing. Walking away from a slow request has to keep working.
    expect(onOpenChange).toHaveBeenCalledWith(false);
  });

  it("dresses a destructive confirm in the destructive button variant", async () => {
    render(ConfirmDialog, { ...base, destructive: true });

    const classes = page.getByRole("button", { name: "Delete" }).element()
      .className;

    // The theme defines --color-destructive but no --color-destructive-foreground,
    // so `text-destructive-foreground` compiles to nothing while still winning the
    // tailwind-merge conflict against the default variant's foreground.
    expect(classes).not.toContain("text-destructive-foreground");
    expect(classes).toContain("text-white");
    expect(classes).toContain("bg-destructive");
    expect(classes).toContain("dark:bg-destructive/60");
    expect(classes).toContain("focus-visible:ring-destructive/20");
  });

  it("leaves a non-destructive confirm on the default variant", async () => {
    render(ConfirmDialog, base);

    const classes = page.getByRole("button", { name: "Delete" }).element()
      .className;

    expect(classes).toContain("bg-primary");
    expect(classes).toContain("text-primary-foreground");
  });
});

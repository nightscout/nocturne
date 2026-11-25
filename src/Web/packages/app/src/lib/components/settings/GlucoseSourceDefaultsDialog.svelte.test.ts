import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, expect, it, vi } from "vitest";
import { GlucoseProcessing, type GlucoseProcessingSourceDefault } from "#lib/api/index.js";
import GlucoseSourceDefaultsDialog from "./GlucoseSourceDefaultsDialog.svelte";

const stored = (): GlucoseProcessingSourceDefault[] => [
  { match: "xDrip", field: "device", processing: GlucoseProcessing.Smoothed },
];

describe("GlucoseSourceDefaultsDialog", () => {
  it("keeps the rules being edited when the stored rules are read again", async () => {
    const view = render(GlucoseSourceDefaultsDialog, {
      open: true,
      rules: stored(),
      onSave: vi.fn(),
      onCancel: vi.fn(),
    });
    await expect.element(page.getByPlaceholder("Prefix to match...")).toBeVisible();

    await page.getByRole("button", { name: /add rule/i }).click();
    expect(page.getByPlaceholder("Prefix to match...").elements()).toHaveLength(2);

    await view.rerender({ rules: stored() });

    expect(page.getByPlaceholder("Prefix to match...").elements()).toHaveLength(2);
  });
});

import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, expect, it } from "vitest";

import Harness from "./RecentTreatmentsCardHarness.test.svelte";

describe("RecentTreatmentsCard", () => {
  it("loads the entry dialog when a treatment row is first clicked", async () => {
    render(Harness, {
      entries: [{ kind: "carbs", data: { id: "carb-1", mills: Date.now() - 60_000, carbs: 30 } }],
    });

    const dialog = page.getByRole("dialog");
    expect(dialog.elements()).toHaveLength(0);

    await page.getByRole("button", { name: /Carbs/ }).click();

    await expect.element(dialog.getByText("Edit Entry")).toBeVisible();
  });
});

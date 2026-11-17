import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach } from "vitest";
import { flushSync } from "svelte";
import CarbRatioFormatSelect from "./CarbRatioFormatSelect.svelte";
import { carbRatioFormat } from "#lib/stores/appearance-store.svelte.js";

describe("CarbRatioFormatSelect", () => {
  beforeEach(() => {
    carbRatioFormat.current = "1:x";
  });

  it("shows the chosen format as 10 g per unit", async () => {
    render(CarbRatioFormatSelect);

    await expect.element(page.getByLabelText("Insulin-to-carb ratio (ICR)")).toHaveTextContent("1:10.0");
  });

  it("offers the same ratio in every format and stores the one picked", async () => {
    render(CarbRatioFormatSelect);

    await page.getByLabelText("Insulin-to-carb ratio (ICR)").click();
    await expect.element(page.getByRole("option", { name: "10.0 g/U" })).toBeVisible();
    await page.getByRole("option", { name: "1.00 U/10 g" }).click();
    flushSync();

    expect(carbRatioFormat.current).toBe("U/10g");
    await expect.element(page.getByLabelText("Insulin-to-carb ratio (ICR)")).toHaveTextContent("1.00 U/10 g");
  });
});

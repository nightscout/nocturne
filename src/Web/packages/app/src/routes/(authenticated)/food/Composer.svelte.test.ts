import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi } from "vitest";
import type { Food } from "$api";
import Harness from "./Composer.test-harness.svelte";

async function fillDraft() {
  await page.getByLabelText("Name").fill("Greek yogurt");
  await page.getByLabelText("Carbs").fill("6");
}

const addAnother = () => page.getByRole("button", { name: /Save & add another/ });

describe("food Composer", () => {
  it("paints the food in beside the buttons after save and add another", async () => {
    const onadd = vi.fn(async (food: Food) => ({ ...food, _id: "f1" }));
    render(Harness, { onadd });
    await fillDraft();

    await addAnother().click();

    await expect.element(page.getByTestId("painted-moment")).toBeVisible();
    await expect.element(page.getByText("Added")).toBeVisible();
    await expect.element(page.getByLabelText("Name")).toHaveValue("");
    expect(onadd).toHaveBeenCalledOnce();
  });

  it("keeps the draft and paints nothing when the add fails", async () => {
    const onclose = vi.fn();
    render(Harness, { onadd: async () => null, onclose });
    await fillDraft();

    await addAnother().click();

    await expect.element(addAnother()).toBeEnabled();
    await expect.element(page.getByLabelText("Name")).toHaveValue("Greek yogurt");
    await expect.element(page.getByTestId("painted-moment")).not.toBeInTheDocument();
    expect(onclose).not.toHaveBeenCalled();
  });
});

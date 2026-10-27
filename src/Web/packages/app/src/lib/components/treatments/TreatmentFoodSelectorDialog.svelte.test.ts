import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi, beforeEach } from "vitest";

let favoriteFoods: unknown[] = [];
let recentFoods: unknown[] = [];
let allFoods: unknown[] = [];

vi.mock("#lib/api/generated/foods.generated.remote.js", () => ({
  getFoods: () => Promise.resolve(allFoods),
  getFavorites: () => Promise.resolve(favoriteFoods),
  getRecentFoods: () => Promise.resolve(recentFoods),
  createFood: vi.fn(),
  updateFood: vi.fn(),
  addFavorite: vi.fn(),
  removeFavorite: vi.fn(),
}));

import TreatmentFoodSelectorDialog from "./TreatmentFoodSelectorDialog.svelte";

const toast = { _id: "f1", type: "food", name: "Toast", carbs: 15, portion: 1, unit: "slice" };

function renderDialog(onSubmit = vi.fn()) {
  render(TreatmentFoodSelectorDialog, {
    props: { open: true, onOpenChange: vi.fn(), onSubmit, totalCarbs: 60, unspecifiedCarbs: 30 },
  });
  return onSubmit;
}

describe("TreatmentFoodSelectorDialog", () => {
  beforeEach(() => {
    favoriteFoods = [];
    recentFoods = [];
    allFoods = [];
  });

  it("opens with nothing selected and the add button disabled", async () => {
    renderDialog();

    await expect.element(page.getByRole("heading", { name: "Add food" })).toBeVisible();
    await expect.element(page.getByRole("button", { name: "Add food" })).toBeDisabled();
  });

  it("adds a recent food by one portion", async () => {
    recentFoods = [toast];
    allFoods = [toast];
    const onSubmit = renderDialog();

    await page.getByRole("option", { name: /Toast/ }).click();

    const add = page.getByRole("button", { name: "Add food" });
    await expect.element(add).toBeEnabled();
    await add.click();

    expect(onSubmit).toHaveBeenCalledTimes(1);
    expect(onSubmit.mock.calls[0][0]).toMatchObject({ foodId: "f1", portions: 1 });
    expect(onSubmit.mock.calls[0][1]).toBe("Toast");
  });

  it("logs carbs without saving a food, with the search text as the note", async () => {
    const onSubmit = renderDialog();

    await page.getByPlaceholder("Search foods...").fill("Birthday cake");
    await page.getByRole("option", { name: "Log without saving food" }).click();

    await expect.element(page.getByText(/Log carbs without creating a food entry/)).toBeVisible();
    const log = page.getByRole("button", { name: "Log carbs" });
    await expect.element(log).toBeDisabled();

    await page.getByLabelText("Carbs (g)").fill("40");
    await expect.element(log).toBeEnabled();
    await log.click();

    expect(onSubmit).toHaveBeenCalledTimes(1);
    expect(onSubmit.mock.calls[0][0]).toMatchObject({ foodId: undefined, carbs: 40, note: "Birthday cake" });
    expect(onSubmit.mock.calls[0][1]).toBe("Birthday cake");
  });
});

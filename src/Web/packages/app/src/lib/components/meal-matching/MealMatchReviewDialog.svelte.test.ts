import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi } from "vitest";
import type { SuggestedMealMatch } from "$lib/api";
import { updatedBy } from "$lib/test-stubs/remote-resource";

const remote = vi.hoisted(() => ({
  getSuggestions: Symbol("getSuggestions"),
  getMeals: Symbol("getMeals"),
}));

vi.mock("$api/generated/mealMatchings.generated.remote", async () => {
  const { remoteCommand, remoteQuery } = await import("$lib/test-stubs/remote-resource");
  return {
    getFoodEntry: () => remoteQuery(() => undefined),
    getSuggestions: remote.getSuggestions,
    acceptMatch: remoteCommand(() => undefined),
    dismissMatch: remoteCommand(() => undefined),
  };
});

vi.mock("$api/generated/nutritions.generated.remote", () => ({
  getMeals: remote.getMeals,
}));

vi.mock("svelte-sonner", () => ({ toast: { success: vi.fn(), error: vi.fn() } }));

import { acceptMatch, dismissMatch } from "$api/generated/mealMatchings.generated.remote";
import MealMatchReviewDialog from "./MealMatchReviewDialog.svelte";

const match: SuggestedMealMatch = {
  foodEntryId: "food-1",
  foodName: "Porridge",
  carbs: 40,
  carbIntakeId: "carb-1",
  carbIntakeCarbs: 40,
  carbIntakeMills: 1_700_000_000_000,
  consumedAt: new Date(1_700_000_000_000).toISOString(),
};

function open() {
  const onComplete = vi.fn();
  render(MealMatchReviewDialog, {
    open: true,
    onOpenChange: vi.fn(),
    match,
    onComplete,
  });
  return { onComplete };
}

describe("MealMatchReviewDialog", () => {
  it("has the server refresh the suggestions and the meals when a match is accepted", async () => {
    const { onComplete } = open();

    await page.getByRole("button", { name: "Accept" }).click();

    await vi.waitFor(() => expect(onComplete).toHaveBeenCalled());
    expect(updatedBy(acceptMatch)).toEqual([[remote.getSuggestions, remote.getMeals]]);
  });

  it("has the server refresh the suggestions when a match is dismissed", async () => {
    const { onComplete } = open();

    await page.getByRole("button", { name: "Dismiss" }).click();

    await vi.waitFor(() => expect(onComplete).toHaveBeenCalled());
    expect(updatedBy(dismissMatch)).toEqual([[remote.getSuggestions]]);
  });
});

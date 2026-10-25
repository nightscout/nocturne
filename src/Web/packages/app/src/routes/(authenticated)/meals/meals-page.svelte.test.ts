import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";
import type { MealEvent, SuggestedMealMatch } from "$lib/api";
import {
  remoteCommand,
  remoteQuery,
  updatedBy,
} from "$lib/test-stubs/remote-resource";

const fixture = (): MealEvent[] => [
  {
    isAttributed: false,
    totalCarbs: 40,
    totalInsulin: 4,
    carbIntakes: [{ id: "carb-1", mills: 1_700_000_000_000, carbs: 40 }],
    boluses: [{ id: "bolus-1", mills: 1_700_000_000_000, insulin: 4 }],
    foods: [],
  } as MealEvent,
];

const server = { meals: fixture() };
const shown = $state({ meals: fixture() });
const mealsResource = remoteQuery(() => shown.meals);
mealsResource.refresh = () => {
  shown.meals = server.meals;
  return Promise.resolve();
};

const suggestions: SuggestedMealMatch[] = [
  {
    foodEntryId: "food-1",
    foodName: "Porridge",
    carbs: 40,
    carbIntakeId: "carb-1",
    matchScore: 0.9,
  },
];

vi.mock("$api/generated/nutritions.generated.remote", () => ({
  getMeals: () => mealsResource,
  addCarbIntakeFood: remoteCommand(() => Promise.resolve()),
  deleteCarbIntakeFood: remoteCommand(() => Promise.resolve()),
  updateCarbIntakeFood: remoteCommand(() => Promise.resolve()),
}));

vi.mock("$api/generated/bolus.generated.remote", () => {
  const form = () => ({ result: undefined, enhance: () => ({}) });
  return {
    create: form(),
    update: form(),
    remove: remoteCommand((id) => {
      server.meals = server.meals.map((meal) => ({
        ...meal,
        boluses: meal.boluses?.filter((bolus) => bolus.id !== id),
      })) as MealEvent[];
      return Promise.resolve();
    }),
  };
});

vi.mock("$api/generated/patientRecords.generated.remote", () => ({
  getInsulins: () => remoteQuery(() => []),
}));

vi.mock("$api/generated/mealMatchings.generated.remote", () => ({
  getSuggestions: () => remoteQuery(() => suggestions),
  getFoodEntry: () => remoteQuery(() => undefined),
  acceptMatch: remoteCommand(() => Promise.resolve()),
  dismissMatch: remoteCommand(() => Promise.resolve()),
}));

vi.mock("sveltekit-search-params", async () => {
  const { writable } = await import("svelte/store");
  return { queryParam: () => writable(null) };
});

vi.mock("svelte-sonner", () => ({
  toast: { success: vi.fn(), error: vi.fn() },
}));

import { getMeals } from "$api/generated/nutritions.generated.remote";
import {
  getSuggestions,
  acceptMatch,
  dismissMatch,
} from "$api/generated/mealMatchings.generated.remote";
import MealsPage from "./+page.svelte";

describe("meals page", () => {
  beforeEach(() => {
    server.meals = fixture();
    shown.meals = server.meals;
    updatedBy(acceptMatch).length = 0;
    updatedBy(dismissMatch).length = 0;
  });

  it("refreshes the suggestions and the meals the page reads when a match is accepted", async () => {
    render(MealsPage, {});

    await page.getByRole("button", { name: "Accept" }).click();

    await vi.waitFor(() =>
      expect(updatedBy(acceptMatch)).toEqual([[getSuggestions, getMeals]])
    );
  });

  it("refreshes the suggestions the page reads when a match is dismissed", async () => {
    render(MealsPage, {});

    await page.getByRole("button", { name: "Dismiss" }).click();

    await vi.waitFor(() =>
      expect(updatedBy(dismissMatch)).toEqual([[getSuggestions]])
    );
  });

  it("drops a deleted bolus from the open insulin dialog", async () => {
    render(MealsPage, {});

    await page.getByRole("button", { name: "4.0U" }).click();
    await page.getByRole("button", { name: "Delete bolus" }).click();
    await page.getByRole("button", { name: "Delete", exact: true }).click();

    await vi.waitFor(() =>
      expect(page.getByRole("button", { name: "Delete bolus" }).query()).toBeNull()
    );
    await expect
      .element(page.getByText("No insulin records for this meal"))
      .toBeVisible();
  });
});

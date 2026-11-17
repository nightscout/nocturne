import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";
import type { MealEvent, SuggestedMealMatch } from "#lib/api/index.js";
import {
  remoteCommand,
  remoteQuery,
  updatedBy,
} from "#lib/test-stubs/remote-resource.js";

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

const suggestionsFixture = (): SuggestedMealMatch[] => [
  {
    foodEntryId: "food-1",
    foodName: "Porridge",
    carbs: 40,
    carbIntakeId: "carb-1",
    matchScore: 0.9,
  },
];

const server = {
  meals: fixture(),
  dismissAnswer: (): Promise<unknown> => Promise.resolve(),
};
const shown = $state({ meals: fixture(), suggestions: suggestionsFixture() });
const mealsResource = remoteQuery(() => shown.meals);
mealsResource.refresh = () => {
  shown.meals = server.meals;
  return Promise.resolve();
};
/** What the meals list holds once an accept has landed on the server. */
let mealsAfterAccept: MealEvent[];

const { toastSuccess } = vi.hoisted(() => ({ toastSuccess: vi.fn() }));

vi.mock("#lib/api/generated/nutritions.generated.remote.js", () => ({
  getMeals: () => mealsResource,
  addCarbIntakeFood: remoteCommand(() => Promise.resolve()),
  deleteCarbIntakeFood: remoteCommand(() => Promise.resolve()),
  updateCarbIntakeFood: remoteCommand(() => Promise.resolve()),
}));

vi.mock("#lib/api/generated/bolus.generated.remote.js", async () => {
  const { remoteFormFields } = await import("#lib/test-stubs/remote-resource.js");
  const form = () => ({ result: undefined, enhance: () => ({}), fields: remoteFormFields() });
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

vi.mock("#lib/api/generated/patientRecords.generated.remote.js", () => ({
  getInsulins: () => remoteQuery(() => []),
}));

// The stub's `updates()` refreshes nothing, so the accept lands the refreshed lists itself.
vi.mock("#lib/api/generated/mealMatchings.generated.remote.js", () => ({
  getSuggestions: () => remoteQuery(() => shown.suggestions),
  getFoodEntry: () => remoteQuery(() => undefined),
  acceptMatch: remoteCommand(() => {
    server.meals = mealsAfterAccept;
    shown.meals = server.meals;
    shown.suggestions = [];
    return Promise.resolve();
  }),
  dismissMatch: remoteCommand(() => server.dismissAnswer()),
}));

vi.mock("sveltekit-search-params", () => ({ queryParameters: () => ({}) }));

vi.mock("svelte-sonner", async (original) => ({
  ...(await original<typeof import("svelte-sonner")>()),
  toast: { success: toastSuccess, error: vi.fn() },
}));
vi.mock("@nocturne/coach", () => ({ coachmark: () => () => {} }));

import { getMeals } from "#lib/api/generated/nutritions.generated.remote.js";
import {
  getSuggestions,
  acceptMatch,
} from "#lib/api/generated/mealMatchings.generated.remote.js";
import MealsPage from "./+page.svelte";

beforeEach(() => {
  toastSuccess.mockReset();
  server.meals = fixture();
  shown.meals = server.meals;
  shown.suggestions = suggestionsFixture();
  mealsAfterAccept = fixture();
  updatedBy(acceptMatch).length = 0;
  server.dismissAnswer = () => Promise.resolve();
});

const accept = () => page.getByRole("button", { name: "Accept" }).click();

describe("meals page", () => {
  it("refreshes the suggestions and the meals the page reads when a match is accepted", async () => {
    render(MealsPage, {});

    await accept();

    await vi.waitFor(() =>
      expect(updatedBy(acceptMatch)).toEqual([[getSuggestions, getMeals]])
    );
  });

  it("takes a dismissed suggestion off the page before the server answers", async () => {
    server.dismissAnswer = () => new Promise(() => {});
    render(MealsPage, {});

    const dismiss = page.getByRole("button", { name: "Dismiss" });
    await expect.element(dismiss).toBeVisible();
    await dismiss.click();

    await vi.waitFor(() => expect(dismiss.elements()).toHaveLength(0));
  });

  it("puts a dismissed suggestion back when the server refuses", async () => {
    let refuse!: (err: unknown) => void;
    server.dismissAnswer = () => new Promise((_, reject) => (refuse = reject));
    render(MealsPage, {});

    const dismiss = page.getByRole("button", { name: "Dismiss" });
    await dismiss.click();
    await vi.waitFor(() => expect(dismiss.elements()).toHaveLength(0));

    refuse(new Error("offline"));

    await expect.element(dismiss).toBeVisible();
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

describe("meals page, accepting a match", () => {
  it("marks the matched meal's row and announces the accept", async () => {
    mealsAfterAccept = fixture().map((meal) => ({ ...meal, isAttributed: true }));
    render(MealsPage, {});

    await accept();

    await expect.element(page.getByRole("status")).toHaveTextContent("Meal match accepted");
    await expect.element(page.getByText("Attributed", { exact: true })).toBeVisible();
    expect(toastSuccess).not.toHaveBeenCalled();
  });

  it("confirms with a toast under the unattributed filter, where the row leaves the list", async () => {
    mealsAfterAccept = [];
    render(MealsPage, {});
    await page.getByRole("button", { name: "Unattributed only" }).click();

    await accept();

    await expect.poll(() => toastSuccess.mock.calls.length).toBe(1);
    expect(toastSuccess).toHaveBeenCalledWith("Meal match accepted");
    await expect.element(page.getByText("No meals found in this range.")).toBeVisible();
  });
});

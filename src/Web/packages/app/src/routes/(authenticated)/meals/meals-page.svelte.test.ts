import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { MealEvent, SuggestedMealMatch } from "$lib/api";
import { fakeRemoteQuery, type FakeQuery } from "$lib/api/fake-remote-query.svelte";

let meals: MealEvent[];
let suggestions: SuggestedMealMatch[];
/**
 * Built and loaded outside the page: the page reads its queries inside
 * `$derived`, where the fake's first load would write state.
 */
let mealsQuery: FakeQuery<MealEvent[]>;
let suggestionsQuery: FakeQuery<SuggestedMealMatch[]>;
/** What the meals list holds once the accept has landed on the server. */
let mealsAfterAccept: MealEvent[];

const { toastSuccess } = vi.hoisted(() => ({ toastSuccess: vi.fn() }));

vi.mock("svelte-sonner", async (original) => ({
  ...(await original<typeof import("svelte-sonner")>()),
  toast: { success: toastSuccess, error: vi.fn() },
}));
vi.mock("@nocturne/coach", () => ({ coachmark: () => () => {} }));
// The picker binds the range to the URL, which a component test has no router for.
vi.mock("$lib/components/ui/date-range-picker.svelte", async () => ({
  default: (await import("$lib/test-stubs/Empty.test-stub.svelte")).default,
}));
vi.mock("$api/generated/nutritions.generated.remote", async (original) => ({
  ...(await original<object>()),
  getMeals: () => mealsQuery,
  addCarbIntakeFood: vi.fn(),
  deleteCarbIntakeFood: vi.fn(),
}));
vi.mock("$api/generated/mealMatchings.generated.remote", async (original) => ({
  ...(await original<object>()),
  getSuggestions: () => suggestionsQuery,
  getFoodEntry: vi.fn(),
  acceptMatch: vi.fn(async () => {
    meals = mealsAfterAccept;
    suggestions = [];
  }),
  dismissMatch: vi.fn(),
}));

import MealsPage from "./+page.svelte";

const unattributed = {
  carbIntakes: [{ id: "c1", mills: Date.UTC(2026, 8, 1, 12, 30), carbs: 45 }],
  foods: [],
  totalCarbs: 45,
  isAttributed: false,
} as unknown as MealEvent;

const match = {
  foodEntryId: "e1",
  carbIntakeId: "c1",
  foodName: "Porridge",
  carbs: 45,
  matchScore: 0.9,
} as SuggestedMealMatch;

beforeEach(async () => {
  toastSuccess.mockReset();
  meals = [unattributed];
  suggestions = [match];
  mealsQuery = fakeRemoteQuery(() => Promise.resolve(meals))();
  suggestionsQuery = fakeRemoteQuery(() => Promise.resolve(suggestions))();
  await Promise.all([mealsQuery.refresh(), suggestionsQuery.refresh()]);
});

const accept = () => page.getByRole("button", { name: "Accept" }).click();

describe("meals page, accepting a match", () => {
  it("marks the matched meal's row and announces the accept", async () => {
    mealsAfterAccept = [{ ...unattributed, isAttributed: true }];
    render(MealsPage);

    await accept();

    await expect.element(page.getByRole("status")).toHaveTextContent("Meal match accepted");
    await expect.element(page.getByText("Attributed", { exact: true })).toBeVisible();
    expect(toastSuccess).not.toHaveBeenCalled();
  });

  it("confirms with a toast under the unattributed filter, where the row leaves the list", async () => {
    mealsAfterAccept = [];
    render(MealsPage);
    await page.getByRole("button", { name: "Unattributed only" }).click();

    await accept();

    await expect.poll(() => toastSuccess.mock.calls.length).toBe(1);
    expect(toastSuccess.mock.calls[0][1]).toMatchObject({ componentProps: { message: "Meal match accepted" } });
    await expect.element(page.getByText("No meals found in this range.")).toBeVisible();
  });
});

import { beforeEach, describe, expect, it, vi } from "vitest";
import type { Food } from "$api";

const { createFood, toastError } = vi.hoisted(() => ({
  createFood: vi.fn<(food: Food) => Promise<Food | undefined>>(),
  toastError: vi.fn(),
}));

vi.mock("svelte-sonner", () => ({ toast: { error: toastError, success: vi.fn() } }));
vi.mock("$api/generated/foods.generated.remote", () => ({
  getFoods: vi.fn(),
  createFood,
  updateFood: vi.fn(),
  getFavorites: vi.fn(),
  addFavorite: vi.fn(),
  removeFavorite: vi.fn(),
  getFoodAttributionCount: vi.fn(),
}));
vi.mock("./data.remote", () => ({ deleteFood: vi.fn() }));

import { FoodState } from "./food-state.svelte";

const draft = { name: "Greek yogurt", carbs: 6, portion: 100, unit: "g" } as Food;

describe("FoodState.addFood", () => {
  beforeEach(() => {
    createFood.mockReset();
    toastError.mockReset();
  });

  it("adds the created food and posts no toast of its own", async () => {
    createFood.mockResolvedValue({ ...draft, _id: "f1" });
    const state = new FoodState();

    await expect(state.addFood(draft)).resolves.toMatchObject({ _id: "f1" });
    expect(state.foods.map((f) => f._id)).toEqual(["f1"]);
    expect(toastError).not.toHaveBeenCalled();
  });

  it("reports a create that comes back without an id as a failure", async () => {
    createFood.mockResolvedValue({ ...draft });
    const state = new FoodState();

    await expect(state.addFood(draft)).resolves.toBeNull();
    expect(state.foods).toEqual([]);
    expect(toastError).toHaveBeenCalledWith("Failed to create food");
  });

  it("reports a rejected create", async () => {
    createFood.mockRejectedValue(new Error("boom"));
    const state = new FoodState();

    await expect(state.addFood(draft)).resolves.toBeNull();
    expect(toastError).toHaveBeenCalledOnce();
  });
});

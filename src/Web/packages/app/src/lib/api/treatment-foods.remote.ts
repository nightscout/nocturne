/**
 * Remote functions for food breakdown workflows on carb intake records
 *
 * Functions that have generated equivalents have been moved to:
 * - nutritions.generated.remote.ts: getCarbIntakeFoods, addCarbIntakeFood, getMeals
 * - foods.generated.remote.ts: getFavorites, addFavorite, removeFavorite, getRecentFoods
 *
 * Kept here: functions with custom logic or broken generated equivalents.
 */
import { getRequestEvent, query, command } from "$app/server";
import { error } from "@sveltejs/kit";
import { z } from "zod";
import { type CarbIntakeFoodRequest, type Food } from "$lib/api";
import { CarbIntakeFoodRequestSchema, FoodSchema } from "$lib/api/generated/schemas";
import { getCarbIntakeFoods } from "$api/generated/nutritions.generated.remote";
import { getRecentFoods } from "$api/generated/foods.generated.remote";

/**
 * Update a food breakdown entry
 * NOTE: Generated version is broken (missing parameters in codegen), so kept here.
 */
export const updateCarbIntakeFood = command(
  z.object({
    carbIntakeId: z.string(),
    entryId: z.string(),
    request: CarbIntakeFoodRequestSchema,
  }),
  async ({ carbIntakeId, entryId, request }) => {
    const { locals } = getRequestEvent();
    const { apiClient } = locals;

    try {
      const result = await apiClient.nutrition.updateCarbIntakeFood(
        carbIntakeId,
        entryId,
        request as CarbIntakeFoodRequest
      );
      await getCarbIntakeFoods(carbIntakeId).refresh();
      return result;
    } catch (err) {
      console.error("Error updating carb intake food:", err);
      throw error(500, "Failed to update food entry");
    }
  }
);

/**
 * Delete a food breakdown entry
 * NOTE: Generated version is broken (missing parameters in codegen), so kept here.
 */
export const deleteCarbIntakeFood = command(
  z.object({
    carbIntakeId: z.string(),
    entryId: z.string(),
  }),
  async ({ carbIntakeId, entryId }) => {
    const { locals } = getRequestEvent();
    const { apiClient } = locals;

    try {
      await apiClient.nutrition.deleteCarbIntakeFood(
        carbIntakeId,
        entryId
      );
      await getCarbIntakeFoods(carbIntakeId).refresh();
      return { success: true };
    } catch (err) {
      console.error("Error deleting carb intake food:", err);
      throw error(500, "Failed to delete food entry");
    }
  }
);

/**
 * Get all food records (type="food")
 */
export const getAllFoods = query(async () => {
  const { locals } = getRequestEvent();
  const { apiClient } = locals;

  try {
    const foods = await apiClient.food.getFood2();
    return foods.filter((food) => food.type === "food");
  } catch (err) {
    console.error("Error loading foods:", err);
    throw error(500, "Failed to load foods");
  }
});

/**
 * Get a single food by ID
 */
export const getFoodById = query(z.string(), async (foodId) => {
  const { locals } = getRequestEvent();
  const { apiClient } = locals;

  try {
    return await apiClient.food.getFoodById(foodId);
  } catch (err) {
    console.error("Error loading food:", err);
    throw error(500, "Failed to load food");
  }
});

/**
 * Create a new food record
 */
export const createNewFood = command(
  FoodSchema,
  async (food) => {
    const { locals } = getRequestEvent();
    const { apiClient } = locals;

    try {
      const result = await apiClient.food.createFood2(food as Food);
      await Promise.all([
        getAllFoods().refresh(),
        getRecentFoods(undefined).refresh(),
      ]);
      return { success: true, record: result[0] };
    } catch (err) {
      console.error("Error creating food:", err);
      throw error(500, "Failed to create food");
    }
  }
);

/**
 * Update an existing food record
 */
export const updateExistingFood = command(FoodSchema, async (food) => {
  const { locals } = getRequestEvent();
  const { apiClient } = locals;
  const f = food as Food;

  try {
    if (!f._id) {
      throw error(400, "Food ID is required for update");
    }
    await apiClient.food.updateFood2(f._id, f);
    await Promise.all([
      getAllFoods().refresh(),
      getFoodById(f._id).refresh(),
      getRecentFoods(undefined).refresh(),
    ]);
    return { success: true, record: f };
  } catch (err) {
    console.error("Error updating food:", err);
    throw error(500, "Failed to update food");
  }
});

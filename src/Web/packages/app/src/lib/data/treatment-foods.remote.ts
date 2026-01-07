/**
 * Remote functions for treatment food breakdown workflows
 */
import { getRequestEvent, query, command } from "$app/server";
import { error } from "@sveltejs/kit";
import { z } from "zod";
import { TreatmentFoodInputMode, type MealTreatment } from "$lib/api";

const treatmentFoodRequestSchema = z.object({
  foodId: z.string().optional(),
  portions: z.number().optional(),
  carbs: z.number().optional(),
  timeOffsetMinutes: z.number().optional(),
  note: z.string().optional(),
  inputMode: z.enum(TreatmentFoodInputMode).optional(),
});

/**
 * Get food breakdown for a treatment
 */
export const getTreatmentFoodBreakdown = query(z.string(), async (treatmentId) => {
  const { locals } = getRequestEvent();
  const { apiClient } = locals;

  try {
    return await apiClient.treatmentFoods.getTreatmentFoods(treatmentId);
  } catch (err) {
    console.error("Error loading treatment foods:", err);
    throw error(500, "Failed to load treatment foods");
  }
});

/**
 * Add a food breakdown entry
 */
export const addTreatmentFood = command(
  z.object({
    treatmentId: z.string(),
    request: treatmentFoodRequestSchema,
  }),
  async ({ treatmentId, request }) => {
    const { locals } = getRequestEvent();
    const { apiClient } = locals;

    try {
      const result = await apiClient.treatmentFoods.addTreatmentFood(treatmentId, request);
      await getTreatmentFoodBreakdown(treatmentId).refresh();
      return result;
    } catch (err) {
      console.error("Error adding treatment food:", err);
      throw error(500, "Failed to add treatment food");
    }
  }
);

/**
 * Update a food breakdown entry
 */
export const updateTreatmentFood = command(
  z.object({
    treatmentId: z.string(),
    entryId: z.string(),
    request: treatmentFoodRequestSchema,
  }),
  async ({ treatmentId, entryId, request }) => {
    const { locals } = getRequestEvent();
    const { apiClient } = locals;

    try {
      const result = await apiClient.treatmentFoods.updateTreatmentFood(
        treatmentId,
        entryId,
        request
      );
      await getTreatmentFoodBreakdown(treatmentId).refresh();
      return result;
    } catch (err) {
      console.error("Error updating treatment food:", err);
      throw error(500, "Failed to update treatment food");
    }
  }
);

/**
 * Delete a food breakdown entry
 */
export const deleteTreatmentFood = command(
  z.object({
    treatmentId: z.string(),
    entryId: z.string(),
  }),
  async ({ treatmentId, entryId }) => {
    const { locals } = getRequestEvent();
    const { apiClient } = locals;

    try {
      await apiClient.treatmentFoods.deleteTreatmentFood(
        treatmentId,
        entryId
      );
      await getTreatmentFoodBreakdown(treatmentId).refresh();
      return { success: true };
    } catch (err) {
      console.error("Error deleting treatment food:", err);
      throw error(500, "Failed to delete treatment food");
    }
  }
);

/**
 * Get treatments with attribution status for meals view
 */
export const getMealTreatments = query(
  z
    .object({
      from: z.string().optional(),
      to: z.string().optional(),
      attributed: z.boolean().optional(),
    })
    .optional(),
  async (params): Promise<MealTreatment[]> => {
    const { locals } = getRequestEvent();
    const { apiClient } = locals;

    const fromDate = params?.from ? new Date(params.from) : undefined;
    const toDate = params?.to ? new Date(params.to) : undefined;

    try {
      return await apiClient.treatmentFoods.getMeals(
        fromDate,
        toDate,
        params?.attributed
      );
    } catch (err) {
      console.error("Error loading meal treatments:", err);
      throw error(500, "Failed to load meals");
    }
  }
);

/**
 * Get favorite foods for the current user
 */
export const getFavoriteFoods = query(async () => {
  const { locals } = getRequestEvent();
  const { apiClient } = locals;

  try {
    return await apiClient.foodsV4.getFavorites();
  } catch (err) {
    console.error("Error loading favorite foods:", err);
    throw error(500, "Failed to load favorites");
  }
});

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
 * Get recent foods for the current user
 */
export const getRecentFoods = query(z.number().optional(), async (limit) => {
  const { locals } = getRequestEvent();
  const { apiClient } = locals;

  try {
    return await apiClient.foodsV4.getRecentFoods(limit);
  } catch (err) {
    console.error("Error loading recent foods:", err);
    throw error(500, "Failed to load recent foods");
  }
});

/**
 * Add a food to favorites
 */
export const addFavoriteFood = command(z.string(), async (foodId) => {
  const { locals } = getRequestEvent();
  const { apiClient } = locals;

  try {
    await apiClient.foodsV4.addFavorite(foodId);
    await getFavoriteFoods().refresh();
    return { success: true };
  } catch (err) {
    console.error("Error adding favorite food:", err);
    throw error(500, "Failed to add favorite");
  }
});

/**
 * Remove a food from favorites
 */
export const removeFavoriteFood = command(z.string(), async (foodId) => {
  const { locals } = getRequestEvent();
  const { apiClient } = locals;

  try {
    await apiClient.foodsV4.removeFavorite(foodId);
    await getFavoriteFoods().refresh();
    return { success: true };
  } catch (err) {
    console.error("Error removing favorite food:", err);
    throw error(500, "Failed to remove favorite");
  }
});

/**
 * Schema for food record create/update
 */
const foodRecordSchema = z.object({
  _id: z.string().optional(),
  type: z.literal("food").default("food"),
  category: z.string(),
  subcategory: z.string(),
  name: z.string(),
  portion: z.number(),
  carbs: z.number(),
  fat: z.number(),
  protein: z.number(),
  energy: z.number(),
  gi: z.number(),
  unit: z.string(),
});

/**
 * Create a new food record
 */
export const createNewFood = command(
  foodRecordSchema.omit({ _id: true }),
  async (food) => {
    const { locals } = getRequestEvent();
    const { apiClient } = locals;

    try {
      const result = await apiClient.food.createFood2(food);
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
export const updateExistingFood = command(foodRecordSchema, async (food) => {
  const { locals } = getRequestEvent();
  const { apiClient } = locals;

  try {
    if (!food._id) {
      throw error(400, "Food ID is required for update");
    }
    await apiClient.food.updateFood2(food._id, food);
    await Promise.all([
      getAllFoods().refresh(),
      getFoodById(food._id).refresh(),
      getRecentFoods(undefined).refresh(),
    ]);
    return { success: true, record: food };
  } catch (err) {
    console.error("Error updating food:", err);
    throw error(500, "Failed to update food");
  }
});

/**
 * Food page remote functions.
 *
 * Most operations use the generated functions from $api/generated/foods.generated.remote
 * directly. This file only exists for the deleteFood override (generated codegen has a
 * parameter issue) and any page-specific server functions.
 */
import { getRequestEvent, command } from '$app/server';
import { z } from 'zod';

/**
 * Delete a food record with attribution handling.
 * Kept here because the generated version has a broken foodId parameter.
 */
export const deleteFood = command(
  z.object({
    foodId: z.string(),
    attributionMode: z.enum(['clear', 'remove']).optional(),
  }),
  async ({ foodId, attributionMode }) => {
    const { locals } = getRequestEvent();
    const { apiClient } = locals;
    await apiClient.foodsV4.deleteFood(foodId, attributionMode);
    return { success: true };
  }
);

/**
 * Remote functions for food database
 */
import { getRequestEvent, query, command } from '$app/server';
import { error } from '@sveltejs/kit';
import { z } from 'zod';

import type { FoodRecord, QuickPickRecord } from './types';
import type { Food } from '$lib/api';
import { FoodSchema } from '$lib/api/generated/schemas';

const quickPickRecordSchema = z.object({
	_id: z.string().optional(),
	type: z.literal('quickpick'),
	name: z.string(),
	foods: z.array(z.any()),
	carbs: z.number(),
	hideafteruse: z.boolean(),
	hidden: z.boolean(),
	position: z.number(),
});

/**
 * Get all food records and quickpicks
 */
export const getFoodData = query(async () => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		const records = await apiClient.food.getFood2();

		// Separate food records and quickpicks, and build categories
		const foodList: FoodRecord[] = [];
		const quickPickList: QuickPickRecord[] = [];
		const categories: Record<string, Record<string, boolean>> = {};

		records.forEach((record) => {
			if (record.type === 'food') {
				foodList.push(record as FoodRecord);

				// Build categories structure
				const foodRecord = record;
				if (foodRecord.category && !categories[foodRecord.category]) {
					categories[foodRecord.category] = {};
				}
				if (foodRecord.category && foodRecord.subcategory) {
					categories[foodRecord.category][foodRecord.subcategory] = true;
				}
			} else if (record.type === 'quickpick') {
				const quickPickRecord = record as QuickPickRecord;
				// Calculate carbs for quickpick
				quickPickRecord.carbs = 0;
				if (quickPickRecord.foods) {
					quickPickRecord.foods.forEach((food) => {
						quickPickRecord.carbs += food.carbs * (food.portions || 1);
					});
				} else {
					quickPickRecord.foods = [];
				}
				quickPickList.push(quickPickRecord);
			}
		});

		// Sort quickpicks by position
		quickPickList.sort((a, b) => (a.position || 99999) - (b.position || 99999));

		return {
			foodList,
			quickPickList,
			categories,
		};
	} catch (err) {
		console.error('Error loading food database:', err);
		throw error(500, 'Failed to load food database');
	}
});

/**
 * Create a new food record
 */
export const createFood = command(FoodSchema, async (food) => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		const result = await apiClient.food.createFood2(food as Food);
		return { success: true, record: result[0] };
	} catch (err) {
		console.error('Error creating food:', err);
		return { success: false, error: 'Failed to create food' };
	}
});

/**
 * Update an existing food record
 */
export const updateFood = command(FoodSchema, async (food) => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;
	const f = food as Food;

	try {
		if (!f._id) {
			return { success: false, error: 'Food ID is required for update' };
		}
		await apiClient.food.updateFood2(f._id, f);
		return { success: true };
	} catch (err) {
		console.error('Error updating food:', err);
		return { success: false, error: 'Failed to update food' };
	}
});

/**
 * Delete a food record by ID
 */
export const deleteFood = command(z.string(), async (id) => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		await apiClient.food.deleteFood2(id);
		return { success: true };
	} catch (err) {
		console.error('Error deleting food:', err);
		return { success: false, error: 'Failed to delete food' };
	}
});

/**
 * Create a new quickpick record
 */
export const createQuickPick = command(quickPickRecordSchema.omit({ _id: true }), async (quickPick) => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		const result = await apiClient.food.createFood2(quickPick as any);
		return { success: true, record: result[0] };
	} catch (err) {
		console.error('Error creating quickpick:', err);
		return { success: false, error: 'Failed to create quickpick' };
	}
});

/**
 * Update an existing quickpick record
 */
export const updateQuickPick = command(quickPickRecordSchema, async (quickPick) => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		if (!quickPick._id) {
			return { success: false, error: 'QuickPick ID is required for update' };
		}
		await apiClient.food.updateFood2(quickPick._id, quickPick as any);
		return { success: true };
	} catch (err) {
		console.error('Error updating quickpick:', err);
		return { success: false, error: 'Failed to update quickpick' };
	}
});

/**
 * Batch save quickpicks (delete marked ones, update positions)
 */
export const saveQuickPicks = command(
	z.object({
		toDelete: z.array(z.string()),
		toUpdate: z.array(quickPickRecordSchema),
	}),
	async ({ toDelete, toUpdate }) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		try {
			// Delete marked quickpicks
			for (const id of toDelete) {
				await apiClient.food.deleteFood2(id);
			}

			// Update all quickpicks
			for (const qp of toUpdate) {
				if (qp._id) {
					await apiClient.food.updateFood2(qp._id, qp as any);
				}
			}

			return { success: true };
		} catch (err) {
			console.error('Error saving quickpicks:', err);
			return { success: false, error: 'Failed to save quickpicks' };
		}
	}
);

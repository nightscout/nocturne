/**
 * Remote functions for food database
 */
import { query } from '$app/server';
import { error } from '@sveltejs/kit';
import { getApiClient } from '$lib/server/api';
import type { FoodRecord, QuickPickRecord } from './types';

/**
 * Get all food records and quickpicks
 */
export const getFoodData = query(async () => {
	const apiClient = getApiClient();

	try {
		const records = await apiClient.food.getFood2();

		// Separate food records and quickpicks, and build categories
		const foodList: FoodRecord[] = [];
		const quickPickList: QuickPickRecord[] = [];
		const categories: Record<string, Record<string, boolean>> = {};

		records.forEach((record) => {
			if (record.type === 'food') {
				foodList.push(record);

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
			nightscoutUrl: process.env.NIGHTSCOUT_URL || 'http://localhost:1612',
		};
	} catch (err) {
		console.error('Error loading food database:', err);
		throw error(500, 'Failed to load food database');
	}
});

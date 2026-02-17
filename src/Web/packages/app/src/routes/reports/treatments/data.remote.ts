/**
 * Remote functions for treatments management
 * Uses SvelteKit's experimental remote function forms pattern for progressive enhancement
 */
import { z } from 'zod';
import { getRequestEvent, form, command, query } from '$app/server';
import { invalid } from '@sveltejs/kit';

import type { Treatment } from '$lib/api';

/**
 * Input schema for date range queries (matches reports layout pattern)
 */
const DateRangeSchema = z.object({
	days: z.number().nullish(),
	from: z.string().nullish(),
	to: z.string().nullish(),
});

function calculateDateRange(input?: z.infer<typeof DateRangeSchema>) {
	let startDate: Date;
	let endDate: Date;

	if (input?.from && input?.to) {
		startDate = new Date(input.from);
		endDate = new Date(input.to);
	} else if (input?.days) {
		endDate = new Date();
		startDate = new Date(endDate);
		startDate.setDate(endDate.getDate() - (input.days - 1));
	} else {
		endDate = new Date();
		startDate = new Date(endDate);
		startDate.setDate(endDate.getDate() - 7);
	}

	startDate.setHours(0, 0, 0, 0);
	endDate.setHours(23, 59, 59, 999);

	return { startDate, endDate };
}

/**
 * Get v4 boluses and carb intakes for the treatments page.
 * Treatment summary comes from the backend via calculateTreatmentSummary.
 */
export const getTreatmentsData = query(
	DateRangeSchema.optional(),
	async (input) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;
		const { startDate, endDate } = calculateDateRange(input);
		const fromMs = startDate.getTime();
		const toMs = endDate.getTime();

		const [bolusResponse, carbResponse] = await Promise.all([
			apiClient.insulin.getBoluses(fromMs, toMs, 10000),
			apiClient.nutrition.getCarbIntakes(fromMs, toMs, 10000),
		]);

		const boluses = bolusResponse.data ?? [];
		const carbIntakes = carbResponse.data ?? [];

		const treatmentSummary = (boluses.length > 0 || carbIntakes.length > 0)
			? await apiClient.statistics.calculateTreatmentSummary({ boluses, carbIntakes })
			: null;

		return {
			boluses,
			carbIntakes,
			treatmentSummary,
			dateRange: {
				from: startDate.toISOString(),
				to: endDate.toISOString(),
			},
		};
	}
);

/**
 * Update a treatment form
 */
export const createTreatmentForm = form(
	z.object({
		treatmentData: z.string().min(1, 'Treatment data is required'),
	}),
	async ({ treatmentData }, issue) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		try {
			const parsedTreatment = JSON.parse(treatmentData) as Treatment;
			const createdTreatment = await apiClient.treatments.createTreatment(parsedTreatment);

			return {
				success: true,
				message: 'Treatment created successfully',
				createdTreatment,
			};
		} catch (error) {
			console.error('Error creating treatment:', error);
			invalid(issue.treatmentData('Failed to create treatment. Please try again.'));
		}
	}
);

export const updateTreatmentForm = form(
	z.object({
		treatmentId: z.string().min(1, 'Treatment ID is required'),
		treatmentData: z.string().min(1, 'Treatment data is required'),
	}),
	async ({ treatmentId, treatmentData }, issue) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		try {
			const parsedTreatment = JSON.parse(treatmentData) as Treatment;
			const updatedTreatment = await apiClient.treatments.updateTreatment(
				treatmentId,
				parsedTreatment
			);

			return {
				success: true,
				message: 'Treatment updated successfully',
				updatedTreatment,
			};
		} catch (error) {
			console.error('Error updating treatment:', error);
			invalid(issue.treatmentData('Failed to update treatment. Please try again.'));
		}
	}
);

/**
 * Delete a single treatment form (v4: dispatches to bolus or carb intake endpoint by kind)
 */
export const deleteTreatmentForm = form(
	z.object({
		treatmentId: z.string().min(1, 'Treatment ID is required'),
		treatmentKind: z.enum(['bolus', 'carbIntake']),
	}),
	async ({ treatmentId, treatmentKind }, issue) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		try {
			if (treatmentKind === 'bolus') {
				await apiClient.insulin.deleteBolus(treatmentId);
			} else {
				await apiClient.nutrition.deleteCarbIntake(treatmentId);
			}

			return {
				success: true,
				message: 'Treatment deleted successfully',
				deletedTreatmentId: treatmentId,
			};
		} catch (error) {
			console.error('Error deleting treatment:', error);
			invalid(issue.treatmentId('Failed to delete treatment. Please try again.'));
		}
	}
);

/**
 * Bulk delete treatments form (v4: dispatches to bolus or carb intake endpoint by kind)
 * Accepts JSON array of { id, kind } objects
 */
export const bulkDeleteTreatmentsForm = form(
	z.object({
		items: z.string().min(1, 'At least one item is required'),
	}),
	async ({ items: itemsString }) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		const items: { id: string; kind: 'bolus' | 'carbIntake' }[] = JSON.parse(itemsString);

		if (items.length === 0) {
			invalid('At least one item is required');
		}

		const deletedIds: string[] = [];
		const failedIds: string[] = [];

		for (const item of items) {
			try {
				if (item.kind === 'bolus') {
					await apiClient.insulin.deleteBolus(item.id);
				} else {
					await apiClient.nutrition.deleteCarbIntake(item.id);
				}
				deletedIds.push(item.id);
			} catch (error) {
				console.error(`Error deleting ${item.kind} ${item.id}:`, error);
				failedIds.push(item.id);
			}
		}

		if (failedIds.length > 0) {
			invalid(`Failed to delete ${failedIds.length} of ${items.length} treatments`);
		}

		return {
			success: true,
			message: `Successfully deleted ${deletedIds.length} treatment${deletedIds.length !== 1 ? 's' : ''}`,
			deletedTreatmentIds: deletedIds,
		};
	}
);

// Keep command versions for programmatic use (backwards compatibility)

/**
 * Create a treatment command (for programmatic use)
 */
export const createTreatment = command(
	z.object({
		treatmentData: z.any(), // Full Treatment object
	}),
	async ({ treatmentData }) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		const createdTreatment = await apiClient.treatments.createTreatment(treatmentData);

		return {
			message: 'Treatment created successfully',
			createdTreatment,
		};
	}
);

/**
 * Update a treatment command (for programmatic use)
 */
export const updateTreatment = command(
	z.object({
		treatmentId: z.string(),
		treatmentData: z.any(), // Full Treatment object
	}),
	async ({ treatmentId, treatmentData }) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		const updatedTreatment = await apiClient.treatments.updateTreatment(treatmentId, treatmentData);

		return {
			message: 'Treatment updated successfully',
			updatedTreatment,
		};
	}
);

/**
 * Delete a single treatment command (v4: dispatches by kind)
 */
export const deleteTreatment = command(
	z.object({ id: z.string(), kind: z.enum(['bolus', 'carbIntake']) }),
	async ({ id, kind }) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		if (kind === 'bolus') {
			await apiClient.insulin.deleteBolus(id);
		} else {
			await apiClient.nutrition.deleteCarbIntake(id);
		}

		return {
			message: 'Treatment deleted successfully',
			deletedTreatmentId: id,
		};
	}
);

/**
 * Bulk delete treatments command (v4: dispatches each item by kind)
 */
export const bulkDeleteTreatments = command(
	z.array(z.object({ id: z.string(), kind: z.enum(['bolus', 'carbIntake']) })),
	async (items) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		const deletedIds: string[] = [];
		const failedIds: string[] = [];

		for (const item of items) {
			try {
				if (item.kind === 'bolus') {
					await apiClient.insulin.deleteBolus(item.id);
				} else {
					await apiClient.nutrition.deleteCarbIntake(item.id);
				}
				deletedIds.push(item.id);
			} catch (err) {
				console.error(`Error deleting ${item.kind} ${item.id}:`, err);
				failedIds.push(item.id);
			}
		}

		if (failedIds.length > 0) {
			return {
				success: false,
				message: `Failed to delete ${failedIds.length} of ${items.length} treatments`,
				deletedTreatmentIds: deletedIds,
				failedTreatmentIds: failedIds,
			};
		}

		return {
			success: true,
			message: `Successfully deleted ${deletedIds.length} treatment${deletedIds.length !== 1 ? 's' : ''}`,
			deletedTreatmentIds: deletedIds,
		};
	}
);

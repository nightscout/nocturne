/**
 * Remote functions for treatments management
 * Uses SvelteKit's experimental remote function forms pattern for progressive enhancement
 */
import { z } from 'zod';
import { getRequestEvent, form, command } from '$app/server';
import { invalid } from '@sveltejs/kit';

import type { Treatment } from '$lib/api';

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
 * Delete a single treatment form
 */
export const deleteTreatmentForm = form(
	z.object({
		treatmentId: z.string().min(1, 'Treatment ID is required'),
	}),
	async ({ treatmentId }, issue) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		try {
			await apiClient.treatments.deleteTreatment(treatmentId);

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
 * Bulk delete treatments form
 * Accepts comma-separated treatment IDs as a single string
 */
export const bulkDeleteTreatmentsForm = form(
	z.object({
		treatmentIds: z.string().min(1, 'At least one treatment ID is required'),
	}),
	async ({ treatmentIds: treatmentIdsString }) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		// Parse the comma-separated IDs
		const treatmentIds = treatmentIdsString.split(',').filter(Boolean);

		if (treatmentIds.length === 0) {
			invalid('At least one treatment ID is required');
		}

		const deletedIds: string[] = [];
		const failedIds: string[] = [];

		// Delete treatments sequentially to avoid overwhelming the server
		for (const treatmentId of treatmentIds) {
			try {
				await apiClient.treatments.deleteTreatment(treatmentId);
				deletedIds.push(treatmentId);
			} catch (error) {
				console.error(`Error deleting treatment ${treatmentId}:`, error);
				failedIds.push(treatmentId);
			}
		}

		if (failedIds.length > 0) {
			invalid(`Failed to delete ${failedIds.length} of ${treatmentIds.length} treatments`);
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
 * Delete a single treatment command (for programmatic use)
 */
export const deleteTreatment = command(z.string(), async (treatmentId) => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	await apiClient.treatments.deleteTreatment(treatmentId);

	return {
		message: 'Treatment deleted successfully',
		deletedTreatmentId: treatmentId,
	};
});

/**
 * Bulk delete treatments command (for programmatic use)
 */
export const bulkDeleteTreatments = command(z.array(z.string()), async (treatmentIds) => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	const deletedIds: string[] = [];
	const failedIds: string[] = [];

	for (const treatmentId of treatmentIds) {
		try {
			await apiClient.treatments.deleteTreatment(treatmentId);
			deletedIds.push(treatmentId);
		} catch (err) {
			console.error(`Error deleting treatment ${treatmentId}:`, err);
			failedIds.push(treatmentId);
		}
	}

	if (failedIds.length > 0) {
		return {
			success: false,
			message: `Failed to delete ${failedIds.length} of ${treatmentIds.length} treatments`,
			deletedTreatmentIds: deletedIds,
			failedTreatmentIds: failedIds,
		};
	}

	return {
		success: true,
		message: `Successfully deleted ${deletedIds.length} treatment${deletedIds.length !== 1 ? 's' : ''}`,
		deletedTreatmentIds: deletedIds,
	};
});

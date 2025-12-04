/**
 * Remote functions for treatments management
 */
import { z } from 'zod';
import { query, command } from '$app/server';
import { getApiClient } from '$lib/server/api';
import type { Treatment } from '$lib/api';
import { calculateTreatmentStats } from '$lib/constants/treatment-categories';

/**
 * Update a treatment
 */
export const updateTreatment = command(
	z.object({
		treatmentId: z.string(),
		treatmentData: z.any(), // Full Treatment object
	}),
	async ({ treatmentId, treatmentData }) => {
		const apiClient = getApiClient();

		const updatedTreatment = await apiClient.treatments.updateTreatment2(
			treatmentId,
			treatmentData
		);

		return {
			message: 'Treatment updated successfully',
			updatedTreatment,
		};
	}
);

/**
 * Delete a single treatment
 */
export const deleteTreatment = command(z.string(), async (treatmentId) => {
	const apiClient = getApiClient();

	await apiClient.treatments.deleteTreatment2(treatmentId);

	return {
		message: 'Treatment deleted successfully',
		deletedTreatmentId: treatmentId,
	};
});

/**
 * Bulk delete treatments
 */
export const bulkDeleteTreatments = command(z.array(z.string()), async (treatmentIds) => {
	const apiClient = getApiClient();

	const deletedIds: string[] = [];
	const failedIds: string[] = [];

	for (const treatmentId of treatmentIds) {
		try {
			await apiClient.treatments.deleteTreatment2(treatmentId);
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

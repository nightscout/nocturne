/**
 * Remote functions for manual compatibility testing
 */
import { getRequestEvent, query } from '$app/server';
import { error } from '@sveltejs/kit';
import type { ManualTestRequest } from '$lib/api';
import { ManualTestRequestSchema } from '$lib/api/generated/schemas';
import { dtoSchema } from '$lib/api/dto-schema';
import { errorStatus } from '$lib/forms/submit-error';

/**
 * Run a manual compatibility test between Nightscout and Nocturne
 */
export const runCompatibilityTest = query(dtoSchema<ManualTestRequest>(ManualTestRequestSchema), async (request) => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;
	try {
		// Call the test endpoint via the API client
		const result = await apiClient.compatibility.testApiComparison(request);
		return result;
	} catch (err) {
		console.error('Error running compatibility test:', err);
		if (errorStatus(err)) {
			throw err;
		}
		throw error(500, 'Failed to run compatibility test');
	}
});

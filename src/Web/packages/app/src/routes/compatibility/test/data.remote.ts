/**
 * Remote functions for manual compatibility testing
 */
import { z } from 'zod';
import { getRequestEvent, query } from '$app/server';
import { error } from '@sveltejs/kit';

const ManualTestRequestSchema = z.object({
	nightscoutUrl: z.string().min(1),
	apiSecret: z.string().optional(),
	queryPath: z.string().min(1),
	method: z.string().optional(),
	requestBody: z.string().optional(),
});

export type ManualTestRequest = z.infer<typeof ManualTestRequestSchema>;


/**
 * Run a manual compatibility test between Nightscout and Nocturne
 */
export const runCompatibilityTest = query(ManualTestRequestSchema, async (request) => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;
	try {
		// Call the test endpoint via the API client
		const result = await apiClient.compatibility.testApiComparison(request);
		return result;
	} catch (err) {
		console.error('Error running compatibility test:', err);
		if ((err as any).status) {
			throw err;
		}
		throw error(500, 'Failed to run compatibility test');
	}
});

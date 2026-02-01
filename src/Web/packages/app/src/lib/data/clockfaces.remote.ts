/**
 * Remote functions for clock face management
 * Uses NSwag-generated types from the backend as the source of truth
 */
import { getRequestEvent, query, command } from '$app/server';
import { z } from 'zod';
import { error } from '@sveltejs/kit';
import type { CreateClockFaceRequest, UpdateClockFaceRequest } from '$api';

/**
 * Get all clock faces for the current user
 */
export const list = query(async () => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;
	try {
		return await apiClient.clockFaces.list();
	} catch (err) {
		console.error('Error loading clock faces:', err);
		throw error(500, 'Failed to load clock faces');
	}
});

/**
 * Get a clock face by ID (public, no auth required)
 */
export const getById = query(z.string(), async (id) => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;
	try {
		return await apiClient.clockFaces.getById(id);
	} catch (err) {
		console.error('Error loading clock face:', err);
		throw error(500, 'Failed to load clock face');
	}
});

/**
 * Get a clock face by ID for editing (requires auth, includes name)
 * Combines list data (for name) with config data
 */
export const getByIdForEdit = query(z.string(), async (id) => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;
	try {
		// Get both the list (for name) and the full config
		const [listItems, publicDto] = await Promise.all([
			apiClient.clockFaces.list(),
			apiClient.clockFaces.getById(id)
		]);

		// Find the item in the list to get the name
		const listItem = listItems.find(item => item.id === id);

		return {
			id: publicDto.id,
			name: listItem?.name ?? 'My Clock Face',
			config: publicDto.config
		};
	} catch (err) {
		console.error('Error loading clock face for edit:', err);
		throw error(500, 'Failed to load clock face');
	}
});

/**
 * Create a new clock face
 * Uses passthrough to allow the full request object without manual schema definition
 */
export const create = command(
	z.object({
		name: z.string(),
		config: z.any()
	}),
	async (request) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;
		try {
			const result = await apiClient.clockFaces.create(request as CreateClockFaceRequest);
			await list().refresh();
			return result;
		} catch (err) {
			console.error('Error creating clock face:', err);
			throw error(500, 'Failed to create clock face');
		}
	}
);

/**
 * Update an existing clock face
 * Uses passthrough to allow the full request object without manual schema definition
 */
export const update = command(
	z.object({
		id: z.string(),
		request: z.object({
			name: z.string().optional(),
			config: z.any().optional()
		})
	}),
	async ({ id, request }) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;
		try {
			const result = await apiClient.clockFaces.update(id, request as UpdateClockFaceRequest);
			await list().refresh();
			return result;
		} catch (err) {
			console.error('Error updating clock face:', err);
			throw error(500, 'Failed to update clock face');
		}
	}
);

/**
 * Delete a clock face
 */
export const remove = command(z.string(), async (id) => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;
	try {
		await apiClient.clockFaces.delete(id);
		await list().refresh();
		return { success: true };
	} catch (err) {
		console.error('Error deleting clock face:', err);
		throw error(500, 'Failed to delete clock face');
	}
});

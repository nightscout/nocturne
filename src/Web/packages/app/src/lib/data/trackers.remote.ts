/**
 * Remote functions for tracker management
 */
import { getRequestEvent, query, command } from '$app/server';
import { z } from 'zod';
import { error } from '@sveltejs/kit';
import {
	TrackerCategory,
	CompletionReason,
	type CreateTrackerDefinitionRequest,
	type UpdateTrackerDefinitionRequest,
	type CompleteTrackerInstanceRequest,
	type AckTrackerRequest,
} from '$api';
import { CreateTrackerDefinitionRequestSchema, UpdateTrackerDefinitionRequestSchema } from '$lib/api/generated/schemas';

/**
 * Get all tracker definitions
 */
export const getDefinitions = query(
	z
		.object({
			category: z.enum(TrackerCategory).optional(),
		})
		.optional(),
	async (params) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;
		const category = params?.category;
		try {
			return await apiClient.trackers.getDefinitions(category);
		} catch (err) {
			console.error('Error loading tracker definitions:', err);
			throw error(500, 'Failed to load tracker definitions');
		}
	}
);

/**
 * Get a specific tracker definition
 */
export const getDefinition = query(z.string(), async (id) => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		return await apiClient.trackers.getDefinition(id);
	} catch (err) {
		console.error('Error loading tracker definition:', err);
		throw error(500, 'Failed to load tracker definition');
	}
});

/**
 * Create a new tracker definition
 */
export const createDefinition = command(
	CreateTrackerDefinitionRequestSchema,
	async (request) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;
		try {
			const result = await apiClient.trackers.createDefinition(request as CreateTrackerDefinitionRequest);
			await getDefinitions(undefined).refresh();
			return result;
		} catch (err) {
			console.error('Error creating tracker definition:', err);
			throw error(500, 'Failed to create tracker definition');
		}
	}
);

/**
 * Update a tracker definition
 */
export const updateDefinition = command(
	UpdateTrackerDefinitionRequestSchema.extend({
		id: z.string(),
	}),
	async ({ id, ...request }) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		try {
			const result = await apiClient.trackers.updateDefinition(id, request as UpdateTrackerDefinitionRequest);
			await Promise.all([
				getDefinitions(undefined).refresh(),
				getDefinition(id).refresh(),
			]);
			return result;
		} catch (err) {
			console.error('Error updating tracker definition:', err);
			throw error(500, 'Failed to update tracker definition');
		}
	}
);

/**
 * Delete a tracker definition
 */
export const deleteDefinition = command(z.string(), async (id) => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		await apiClient.trackers.deleteDefinition(id);
		await getDefinitions(undefined).refresh();
		return { success: true };
	} catch (err) {
		console.error('Error deleting tracker definition:', err);
		throw error(500, 'Failed to delete tracker definition');
	}
});

/**
 * Get active tracker instances
 */
export const getActiveInstances = query(async () => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		return await apiClient.trackers.getActiveInstances();
	} catch (err) {
		console.error('Error loading active tracker instances:', err);
		throw error(500, 'Failed to load active tracker instances');
	}
});

/**
 * Get completed tracker instances (history)
 */
export const getInstanceHistory = query(z.number().optional(), async (limit) => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		return await apiClient.trackers.getInstanceHistory(limit ?? 100);
	} catch (err) {
		console.error('Error loading tracker history:', err);
		return [];
	}
});

/**
 * Get upcoming tracker expirations for calendar
 */
export const getUpcomingInstances = query(
	z
		.object({
			from: z.date().optional(),
			to: z.date().optional(),
		})
		.optional(),
	async (params) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		try {
			return await apiClient.trackers.getUpcomingInstances(params?.from, params?.to);
		} catch (err) {
			console.error('Error loading upcoming tracker instances:', err);
			throw error(500, 'Failed to load upcoming tracker instances');
		}
	}
);

/**
 * Start a new tracker instance
 */
export const startInstance = command(
	z.object({
		definitionId: z.string(),
		startNotes: z.string().optional(),
		startTreatmentId: z.string().optional(),
		startedAt: z.date().optional(),
	}),
	async (request) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		try {
			const result = await apiClient.trackers.startInstance(request);
			await Promise.all([
				getActiveInstances().refresh(),
				getInstanceHistory(undefined).refresh(),
			]);
			return result;
		} catch (err) {
			console.error('Error starting tracker instance:', err);
			throw error(500, 'Failed to start tracker instance');
		}
	}
);

/**
 * Complete a tracker instance
 */
export const completeInstance = command(
	z.object({
		id: z.string(),
		request: z.object({
			reason: z.enum(CompletionReason),
			completionNotes: z.string().optional(),
			completeTreatmentId: z.string().optional(),
			completedAt: z.date().optional(),
		}),
	}),
	async ({ id, request }) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		try {
			const result = await apiClient.trackers.completeInstance(
				id,
				request as CompleteTrackerInstanceRequest
			);
			await Promise.all([
				getActiveInstances().refresh(),
				getInstanceHistory(undefined).refresh(),
			]);
			return result;
		} catch (err) {
			console.error('Error completing tracker instance:', err);
			throw error(500, 'Failed to complete tracker instance');
		}
	}
);

/**
 * Acknowledge/snooze a tracker instance
 */
export const ackInstance = command(
	z.object({
		id: z.string(),
		request: z.object({
			snoozeMins: z.number().optional(),
			global: z.boolean().optional(),
		}),
	}),
	async ({ id, request }) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		try {
			await apiClient.trackers.ackInstance(id, request as AckTrackerRequest);
			await getActiveInstances().refresh();
			return { success: true };
		} catch (err) {
			console.error('Error acknowledging tracker instance:', err);
			throw error(500, 'Failed to acknowledge tracker instance');
		}
	}
);

/**
 * Delete a tracker instance
 */
export const deleteInstance = command(z.string(), async (id) => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		await apiClient.trackers.deleteInstance(id);
		await Promise.all([
			getActiveInstances().refresh(),
			getInstanceHistory(undefined).refresh(),
		]);
		return { success: true };
	} catch (err) {
		console.error('Error deleting tracker instance:', err);
		throw error(500, 'Failed to delete tracker instance');
	}
});

/**
 * Get all presets
 */
export const getPresets = query(async () => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		return await apiClient.trackers.getPresets();
	} catch (err) {
		console.error('Error loading tracker presets:', err);
		throw error(500, 'Failed to load tracker presets');
	}
});

/**
 * Apply a preset (starts a new instance)
 */
export const applyPreset = command(
	z.object({
		id: z.string(),
		request: z
			.object({
				overrideNotes: z.string().optional(),
			})
			.optional(),
	}),
	async ({ id, request }) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		try {
			const result = await apiClient.trackers.applyPreset(id, (request ?? {}));
			await Promise.all([
				getActiveInstances().refresh(),
				getInstanceHistory(undefined).refresh(),
			]);
			return result;
		} catch (err) {
			console.error('Error applying tracker preset:', err);
			throw error(500, 'Failed to apply tracker preset');
		}
	}
);

/**
 * Create a new preset
 */
export const createPreset = command(
	z.object({
		name: z.string(),
		definitionId: z.string(),
		defaultStartNotes: z.string().optional(),
	}),
	async (request) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		try {
			const result = await apiClient.trackers.createPreset(request);
			await getPresets().refresh();
			return result;
		} catch (err) {
			console.error('Error creating tracker preset:', err);
			throw error(500, 'Failed to create tracker preset');
		}
	}
);

/**
 * Delete a preset
 */
export const deletePreset = command(z.string(), async (id) => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		await apiClient.trackers.deletePreset(id);
		await getPresets().refresh();
		return { success: true };
	} catch (err) {
		console.error('Error deleting tracker preset:', err);
		throw error(500, 'Failed to delete tracker preset');
	}
});

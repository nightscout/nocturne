/**
 * Shared remote functions for injectable medications used across the application
 */
import { z } from 'zod';
import { getRequestEvent, query } from '$app/server';
import { error } from '@sveltejs/kit';
import type { InjectableMedication, InjectableDose } from '$lib/api';

/**
 * Get active (non-archived) medications for use in selectors and panels
 */
export const getActiveMedications = query(
	z.void(),
	async () => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		try {
			const medications = await apiClient.injectableMedications.getAll(false);
			return (medications ?? []) as InjectableMedication[];
		} catch (err) {
			console.error('Error loading active medications:', err);
			throw error(500, 'Failed to load medications');
		}
	}
);

/**
 * Get recent doses for active medications panel (last 48 hours)
 */
export const getRecentDoses = query(
	z.void(),
	async () => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		try {
			const now = Date.now();
			const fortyEightHoursAgo = now - 48 * 60 * 60 * 1000;

			const doses = await apiClient.injectableDoses.getAll(
				fortyEightHoursAgo,
				now,
				undefined
			);
			return (doses ?? []) as InjectableDose[];
		} catch (err) {
			console.error('Error loading recent doses:', err);
			throw error(500, 'Failed to load recent doses');
		}
	}
);

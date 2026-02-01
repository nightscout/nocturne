/**
 * Remote functions for in-app notification management
 */
import { getRequestEvent, query, command } from '$app/server';
import { z } from 'zod';
import { error } from '@sveltejs/kit';

/**
 * Get all active notifications for the current user
 */
export const getNotifications = query(async () => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		return await apiClient.v2Notifications.getNotifications();
	} catch (err) {
		console.error('Error loading notifications:', err);
		throw error(500, 'Failed to load notifications');
	}
});

/**
 * Execute an action on a notification
 */
export const executeAction = command(
	z.object({
		id: z.string(),
		actionId: z.string(),
	}),
	async ({ id, actionId }) => {
		const { locals } = getRequestEvent();
		const { apiClient } = locals;

		try {
			await apiClient.v2Notifications.executeAction(id, actionId);
			await getNotifications().refresh();
			return { success: true };
		} catch (err) {
			console.error('Error executing notification action:', err);
			throw error(500, 'Failed to execute notification action');
		}
	}
);

/**
 * Dismiss a notification
 */
export const dismissNotification = command(z.string(), async (id) => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		await apiClient.v2Notifications.dismissNotification(id);
		await getNotifications().refresh();
		return { success: true };
	} catch (err) {
		console.error('Error dismissing notification:', err);
		throw error(500, 'Failed to dismiss notification');
	}
});

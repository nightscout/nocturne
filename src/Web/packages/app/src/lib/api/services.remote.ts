/**
 * Remote functions for services management
 * Custom error handling variants that return error objects instead of throwing
 */
import { getRequestEvent, command, query } from '$app/server';
import { error } from '@sveltejs/kit';
import { z } from 'zod';
import { getServicesOverview } from '$api/generated/services.generated.remote';

/** Gets the current status and metrics for all registered connectors */
export const getConnectorStatuses = query(async () => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;
	try {
		return await apiClient.connectorStatus.getStatus();
	} catch (err) {
		console.error('Error in connectorStatus.getStatus:', err);
		throw error(500, 'Failed to get status');
	}
});

/**
 * Delete demo data
 */
export const deleteDemoData = command(async () => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		const result = await apiClient.services.deleteDemoData();
		await getServicesOverview(undefined).refresh();
		return {
			success: result.success ?? false,
			entriesDeleted: result.entriesDeleted,
			error: result.error ?? undefined,
		};
	} catch (err) {
		console.error('Error deleting demo data:', err);
		return {
			success: false,
			error: err instanceof Error ? err.message : 'Failed to delete demo data',
		};
	}
});

/**
 * Delete data source data
 */
export const deleteDataSourceData = command(z.string(), async (dataSourceId) => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		const result = await apiClient.services.deleteDataSourceData(dataSourceId);
		await getServicesOverview(undefined).refresh();
		return {
			success: result.success ?? false,
			entriesDeleted: result.entriesDeleted,
			error: result.error ?? undefined,
		};
	} catch (err) {
		console.error('Error deleting data source:', err);
		return {
			success: false,
			error: err instanceof Error ? err.message : 'Failed to delete data',
		};
	}
});

/**
 * Delete connector data
 */
export const deleteConnectorData = command(z.string(), async (connectorId) => {
	const { locals } = getRequestEvent();
	const { apiClient } = locals;

	try {
		const result = await apiClient.services.deleteConnectorData(connectorId);
		// Refresh both services overview and connector statuses to update counts everywhere
		await Promise.all([
			getServicesOverview(undefined).refresh(),
			getConnectorStatuses(undefined).refresh(),
		]);
		const entriesDeleted = result.entriesDeleted ?? 0;
		const treatmentsDeleted = result.treatmentsDeleted ?? 0;
		const deviceStatusDeleted = result.deviceStatusDeleted ?? 0;
		return {
			success: result.success ?? false,
			entriesDeleted,
			treatmentsDeleted,
			deviceStatusDeleted,
			totalDeleted: entriesDeleted + treatmentsDeleted + deviceStatusDeleted,
			error: result.error ?? undefined,
		};
	} catch (err) {
		console.error('Error deleting connector data:', err);
		return {
			success: false,
			error: err instanceof Error ? err.message : 'Failed to delete connector data',
		};
	}
});
